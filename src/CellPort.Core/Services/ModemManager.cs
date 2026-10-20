using CellPort.Core.At;
using CellPort.Core.Models;
using CellPort.Core.Transport;

namespace CellPort.Core.Services;

/// <summary>
/// 模块管理器：负责发现设备、建立 AT 通道、装配各项服务、处理主动上报分发。
/// 这是上层界面与硬件之间的唯一入口。
/// </summary>
public sealed class ModemManager : IAsyncDisposable
{
    private readonly IAtChannelProvider _channels;

    public ModemManager(IAtChannelProvider channels)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
    }

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    /// <summary>
    /// 串口探测结果的进程内缓存。热插拔后 COM 号会漂移，但通常仍落在少数几个口上，
    /// 因此把「上次成功的口」放在列表首位优先尝试，能大幅缩短连接耗时。
    /// 该缓存还会由宿主（App 层）持久化到磁盘，实现跨进程复用。
    /// </summary>
    private readonly List<string> _portHints = [];

    /// <summary>
    /// 由宿主注入的「记住上次成功串口」回调，用于把结果持久化到配置文件。
    /// 让 Core 层保持对 App 层的零依赖。
    /// </summary>
    public Action<string>? RememberPort { get; set; }

    /// <summary>
    /// 注入历史成功端口（最新的在前），供连接时优先尝试。
    /// 由宿主在启动时从配置读取后调用。
    /// </summary>
    public void PrimePortHints(IEnumerable<string?> hints)
    {
        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h))
            {
                continue;
            }

            _portHints.RemoveAll(p => _channels.PortNameComparer.Equals(p, h));
            _portHints.Add(h);
        }
    }

    /// <summary>设备状态变化。</summary>
    public event EventHandler<DeviceStatus>? StatusUpdated;

    /// <summary>连接状态变化。</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <summary>收到的任意主动上报（用于日志/调试）。</summary>
    public event EventHandler<string>? UnsolicitedLine;

    /// <summary>连接过程中的阶段性诊断信息（供诊断工具/界面显示）。</summary>
    public event EventHandler<string>? TraceMessage;

    /// <summary>
    /// 阶段性诊断信息：通知界面显示，同时落文件日志（AppLog 未初始化时自动 no-op）。
    /// </summary>
    private void Trace(string message)
    {
        TraceMessage?.Invoke(this, message);
        AppLog.Write("TRACE", message);
    }

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => Engine?.Channel.IsOpen == true;

    /// <summary>当前状态快照。</summary>
    public DeviceStatus Status { get; private set; } = new();

    /// <summary>呼叫快照跟踪器；每次模块连接都会创建新的 module generation。</summary>
    public CallTracker CallTracker { get; } = new();

    /// <summary>短信服务。</summary>
    public SmsService? Sms { get; private set; }

    /// <summary>通话服务。</summary>
    public CallService? Calls { get; private set; }

    /// <summary>Shared with the audio session service so legacy module writes cannot bypass an owned session.</summary>
    public IModuleOperationAdmission? OperationAdmission { get; set; }

    /// <summary>设备信息服务。</summary>
    public DeviceInfoService? Info { get; private set; }

    /// <summary>USSD 会话服务。</summary>
    public CusdService? Cusd { get; private set; }

    /// <summary>SIM 电话簿服务。</summary>
    public PhonebookService? Phonebook { get; private set; }

    /// <summary>当前 AT 通道描述。</summary>
    public string ChannelDescription => Engine?.Channel.Description ?? "未连接";

    /// <summary>
    /// 当前 AT 引擎。供诊断工具直接构造通道级客户端（如 LogicalChannelEs10）；
    /// 未连接时为 null。
    /// </summary>
    public AtEngine? Engine { get; private set; }

    /// <summary>
    /// 连接模块。按 UsbDirect → SerialPort 的顺序尝试。
    /// </summary>
    /// <param name="preferredSerialPort">指定串口（用户手动选择时使用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>成功返回 true。</returns>
    public async Task<bool> ConnectAsync(string? preferredSerialPort = null, CancellationToken ct = default)
    {
        // 连接过程串行化：界面开机自动连接与用户手动点击可能重叠，
        // 而进入连接流程的第一步就是 DisconnectAsync，
        // 会把另一个流程刚打开的通道释放掉，表现为「莫名其妙的连接失败」。
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ConnectCoreAsync(preferredSerialPort, ct).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private async Task<bool> ConnectCoreAsync(string? preferredSerialPort, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        IAtChannel? channel = null;
        string? serialPort = null;

        // 1) 优先尝试 USB 直控（WinUSB）
        //    若 MI_02 尚未绑定 WinUSB，会在枚举阶段就返回 null（很快）。
        //    这里额外加超时，防止个别驱动状态下 SetupAPI 调用异常拖慢连接。
        Trace("[1/5] 尝试平台 USB 直控通道…");
        try
        {
            var usbTask = _channels.TryOpenUsbAsync(ct);
            var done = await Task.WhenAny(usbTask, Task.Delay(TimeSpan.FromSeconds(4), ct))
                .ConfigureAwait(false);
            if (done == usbTask)
            {
                var usb = await usbTask.ConfigureAwait(false);
                if (usb is not null)
                {
                    channel = usb;
                    Trace("      USB 直控可用。");
                }
                else
                {
                    Trace("      平台未提供可用的 USB 直控通道，转串口。");
                }
            }
            else
            {
                // 超时后探测仍可能在后台完成并返回一个已 claim 的接口。
                // 必须在其完成时释放，否则句柄与 claim 永久持有（macOS 上表现为「第二次连不上」）。
                DisposeChannelWhenCompleted(usbTask);
                Trace("      USB 探测超时，转串口。");
            }
        }
        catch (Exception ex)
        {
            Trace($"      USB 探测异常：{ex.GetType().Name}，转串口。");
        }

        async Task SelectSerialChannelAsync()
        {
            string? port;
            if (preferredSerialPort is not null)
            {
                port = preferredSerialPort;
                Trace($"[2/5] 使用指定串口：{port}");
            }
            else
            {
                Trace("[2/5] 自动探测串口…");
                using var detectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var detectTask = _channels.DetectSerialAsync(_portHints, detectCts.Token);
                var done2 = await Task.WhenAny(detectTask, Task.Delay(TimeSpan.FromSeconds(20), ct))
                    .ConfigureAwait(false);
                if (done2 == detectTask)
                {
                    port = await detectTask.ConfigureAwait(false);
                }
                else
                {
                    // 取消探测，让探测协程在 catch/finally 路径上归还已打开的串口，
                    // 避免与随后的真实连接争用端口。
                    await detectCts.CancelAsync().ConfigureAwait(false);
                    ObserveDetached(detectTask);
                    port = null;
                }
                Trace(port is null ? "      未找到 AT 串口。" : $"      找到 AT 串口：{port}");
            }

            if (port is not null)
            {
                channel = _channels.CreateSerial(port);
                serialPort = port;
            }
        }

        // 2) 回退到串口
        if (channel is null)
        {
            await SelectSerialChannelAsync().ConfigureAwait(false);
        }

        if (channel is null)
        {
            Trace("[X] 无可用通道。");
            return false;
        }

        var selectedUsb = channel.Kind == AtChannelKind.UsbDirect;
        Trace($"[3/5] 打开通道：{channel.Description}");
        AtEngine? engine = null;
        try
        {
            engine = await OpenAndProbeAsync(channel, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace($"      通道打开或 AT 探测失败：{ex.GetType().Name} {ex.Message}");
        }

        if (engine is null && selectedUsb)
        {
            // USB 接口枚举或描述符只能缩小候选范围，不能保证它真的回 AT。
            // 若探测失败，继续走串口通道，避免错误的 MI_02/MI_03 阻断连接。
            Trace("      USB 通道未通过 AT 探测，改用串口探测。");
            channel = null;
            serialPort = null;
            await SelectSerialChannelAsync().ConfigureAwait(false);
            if (channel is not null)
            {
                try
                {
                    Trace($"[3/5] 打开串口通道：{channel.Description}");
                    engine = await OpenAndProbeAsync(channel, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Trace($"      串口打开或 AT 探测失败：{ex.GetType().Name} {ex.Message}");
                }
            }
        }

        if (engine is null)
        {
            Trace("      AT 通道无响应。");
            return false;
        }

        engine.IoTrace += (_, msg) => Trace($"      IO {msg}");
        Trace("      AT 响应正常。");

        // 只有确认 AT 真的可用后才记住端口：此前写入会把坏端口永久置顶，
        // 拖慢之后每一次连接。
        if (serialPort is not null)
        {
            RememberSuccessfulPort(serialPort);
        }

        Engine = engine;
        CallTracker.BeginModuleSession();

        var sms = new SmsService(engine);
        var calls = new CallService(engine, CallTracker);
        Sms = sms;
        Calls = calls;
        Info = new DeviceInfoService(engine);
        Cusd = new CusdService(engine);
        Phonebook = new PhonebookService(engine);

        try
        {
            await sms.InitializeAsync(ct).ConfigureAwait(false);
            await calls.InitializeAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            CallTracker.MarkModuleDisconnected();
            Sms = null;
            Calls = null;
            Info = null;
            Cusd = null;
            Phonebook = null;
            if (ReferenceEquals(Engine, engine))
            {
                Engine = null;
            }
            await engine.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // 4) 启动主动上报泵
        // 令牌源先落到局部变量：闭包若读字段，可能在断开后读到 null。
        var pumpCts = new CancellationTokenSource();
        _pumpCts = pumpCts;
        _pumpTask = Task.Run(() => PumpAsync(pumpCts.Token), CancellationToken.None);

        ConnectionChanged?.Invoke(this, true);

        // 5) 首次状态读取
        await RefreshStatusAsync(ct).ConfigureAwait(false);

        // 6) 同步已存短信
        try
        {
            await sms.SyncStoredAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消不影响连接结果
        }
        catch (Exception ex)
        {
            // 同步失败不影响连接
            AppLog.Warn($"首次短信同步失败：{ex.GetType().Name} {ex.Message}");
        }

        return true;
    }

    private static async Task<AtEngine?> OpenAndProbeAsync(IAtChannel channel, CancellationToken ct)
    {
        AtEngine? engine = null;
        var keep = false;
        try
        {
            await channel.OpenAsync(ct).ConfigureAwait(false);
            engine = new AtEngine(channel);
            var probe = await engine.ExecuteAsync("AT", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (!probe.Succeeded)
            {
                return null;
            }

            keep = true;
            return engine;
        }
        finally
        {
            if (!keep)
            {
                if (engine is not null)
                {
                    await engine.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>断开连接。</summary>
    public async Task DisconnectAsync()
    {
        CallTracker.MarkModuleDisconnected();

        // 先取消泵，再等待它退出，最后才释放引擎：
        // 否则泵协程可能在引擎已 Dispose 后仍尝试 DrainUnsolicited。
        var pumpTask = _pumpTask;
        _pumpTask = null;

        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 超时或泵内部异常都不应阻塞断开流程。
            }
        }

        // 统一置空全部服务引用：只清一半会留下指向已释放引擎的悬垂服务。
        Sms = null;
        Calls = null;
        Info = null;
        Cusd = null;
        Phonebook = null;
        Status = new DeviceStatus();

        var engine = Engine;
        Engine = null;
        if (engine is not null)
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        ConnectionChanged?.Invoke(this, false);
    }

    /// <summary>
    /// 记录一次确认可用的串口：进程内提到列表首位，并通知宿主持久化。
    /// </summary>
    private void RememberSuccessfulPort(string port)
    {
        _portHints.RemoveAll(p => _channels.PortNameComparer.Equals(p, port));
        _portHints.Insert(0, port);
        if (_portHints.Count > 4)
        {
            _portHints.RemoveRange(4, _portHints.Count - 4);
        }

        try
        {
            RememberPort?.Invoke(port);
        }
        catch
        {
            // 持久化失败不影响连接
        }
    }

    /// <summary>
    /// 探测已超时但任务仍会完成时调用：任务返回的通道必须释放，
    /// 否则底层句柄与接口声明会一直被持有。
    /// </summary>
    private static void DisposeChannelWhenCompleted(Task<IAtChannel?> task)
    {
        _ = task.ContinueWith(static t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
            {
                if (t.Result is { } late)
                {
                    _ = late.DisposeAsync().AsTask();
                }
                return;
            }

            _ = t.Exception; // 观察异常，避免未观测任务异常
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>观察一个已放弃等待的任务，避免未观测异常。</summary>
    private static void ObserveDetached(Task task)
    {
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>刷新状态。</summary>
    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        // 捕获到局部变量：await 期间字段可能被 DisconnectAsync 置空。
        var info = Info;
        if (info is null)
        {
            return;
        }
        try
        {
            Status = await info.ReadStatusAsync(ct).ConfigureAwait(false);
            StatusUpdated?.Invoke(this, Status);
        }
        catch (OperationCanceledException)
        {
            // 取消：保留上次快照。
        }
        catch (Exception ex)
        {
            // 刷新失败保留上次快照，但留下可排障的日志。
            AppLog.Warn($"状态刷新失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>执行任意 AT 指令（供控制台使用）。</summary>
    public async Task<AtResult> ExecuteAtAsync(string command, CancellationToken ct = default)
    {
        // 捕获到局部变量：await 期间字段可能被 DisconnectAsync 置空。
        var engine = Engine;
        if (engine is null)
        {
            return new AtResult(command, false, [], "未连接模块。", TimeSpan.Zero,
                AtCompletionStatus.TransportFailure);
        }
        var operation = ClassifyAtCommand(command);
        var admission = await TryAcquireOperationAsync(operation.Kind, operation.RawCommandIsReadOnly, ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new AtResult(command, false, [], admission.ErrorCode ?? "OperationConflict", TimeSpan.Zero,
                AtCompletionStatus.OperationRejected);
        await using var lease = admission.Lease;
        return await engine.ExecuteAsync(command, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    public async ValueTask<ModuleOperationAdmissionResult> TryAcquireOperationAsync(
        ModuleOperationKind kind,
        bool rawCommandIsReadOnly = false,
        CancellationToken cancellationToken = default)
    {
        if (OperationAdmission is null) return ModuleOperationAdmissionResult.Permit();
        return await OperationAdmission.TryAcquireAsync(kind, rawCommandIsReadOnly, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> SendSmsAsync(string number, string body, CancellationToken ct = default)
    {
        // 捕获到局部变量：await 期间字段可能被 DisconnectAsync 置空。
        var sms = Sms;
        if (sms is null) return false;
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.SmsWrite, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed) return false;
        await using var lease = admission.Lease;
        return await sms.SendAsync(number, body, ct).ConfigureAwait(false);
    }

    public async Task<CusdService.CusdResult> SendUssdAsync(string code, CancellationToken ct = default)
    {
        // 捕获到局部变量：await 期间字段可能被 DisconnectAsync 置空。
        var cusd = Cusd;
        if (cusd is null) return new CusdService.CusdResult(false, null, "模块未连接");
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.RawConsoleCommand, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new CusdService.CusdResult(false, null, $"操作被仲裁器拒绝：{admission.ErrorCode}");
        await using var lease = admission.Lease;
        return await cusd.SendAsync(code, ct).ConfigureAwait(false);
    }

    public Task<bool> DialAsync(string number, CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.DialAsync(number, ct), ct);

    public Task<bool> AnswerAsync(CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.AnswerAsync(ct), ct);

    public Task<bool> HangUpAsync(CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.HangUpAsync(ct), ct);

    public Task<bool> SendDtmfAsync(char digit, CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.SendDtmfAsync(digit, ct), ct);

    public async Task<LogicalChannelEs10.ProfileStateResult> SetEsimProfileStateAsync(
        string profileIdentifier, bool enable, CancellationToken ct = default)
    {
        var es10 = Info?.Es10;
        if (es10 is null)
            return new LogicalChannelEs10.ProfileStateResult(false, -1, "eUICC 通道未就绪");

        var admission = await TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new LogicalChannelEs10.ProfileStateResult(false, -2, $"操作被仲裁器拒绝：{admission.ErrorCode}");
        await using var lease = admission.Lease;

        var activeIccidBefore = await ReadCurrentIccidAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(activeIccidBefore))
            UpdateCurrentIccid(activeIccidBefore);
        if (enable)
        {
            if (string.Equals(activeIccidBefore, profileIdentifier, StringComparison.Ordinal))
                return new LogicalChannelEs10.ProfileStateResult(true, 0, "Profile 已启用");
        }

        if (es10.Channel <= 0)
        {
            var opened = await es10.OpenIsdrAsync(ct: ct).ConfigureAwait(false);
            if (opened is null or <= 0)
                return new LogicalChannelEs10.ProfileStateResult(false, -1, "ISD-R 逻辑通道无法打开");
        }
        try
        {
            LogicalChannelEs10.ProfileStateResult result;
            const int maxCatBusyRetries = 3;
            for (var attempt = 0; ; attempt++)
            {
                result = await es10.SetProfileStateAsync(profileIdentifier, enable, ct).ConfigureAwait(false);
                if (!enable || result.Code != 5 || attempt >= maxCatBusyRetries)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(800), ct).ConfigureAwait(false);
            }

            if (result.Ok)
            {
                if (enable)
                    UpdateCurrentIccid(profileIdentifier);
                else if (string.Equals(activeIccidBefore, profileIdentifier, StringComparison.Ordinal))
                    UpdateCurrentIccid(null);
                return result;
            }

            // refresh=true can reset the UICC before the modem returns the APDU response.
            // Confirm the requested state from a fresh profile list before reporting failure.
            if (result.Code < 0)
            {
                await es10.CloseAsync().ConfigureAwait(false);
                var expectedState = enable ? 1 : 0;
                if (await WaitForEsimProfileStateAsync(es10, profileIdentifier, expectedState, ct).ConfigureAwait(false))
                {
                    if (enable)
                        UpdateCurrentIccid(profileIdentifier);
                    else if (string.Equals(activeIccidBefore, profileIdentifier, StringComparison.Ordinal))
                        UpdateCurrentIccid(null);
                    return new LogicalChannelEs10.ProfileStateResult(true, 0, "Profile 状态已确认");
                }
            }

            return result;
        }
        finally
        {
            await es10.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<LogicalChannelEs10.ProfileStateResult> DeleteEsimProfileAsync(
        string iccid, CancellationToken ct = default)
    {
        var es10 = Info?.Es10;
        if (es10 is null)
            return new LogicalChannelEs10.ProfileStateResult(false, -1, "eUICC 通道未就绪");
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new LogicalChannelEs10.ProfileStateResult(false, -2, $"操作被仲裁器拒绝：{admission.ErrorCode}");
        await using var lease = admission.Lease;
        if (es10.Channel <= 0)
        {
            var opened = await es10.OpenIsdrAsync(ct: ct).ConfigureAwait(false);
            if (opened is null or <= 0)
                return new LogicalChannelEs10.ProfileStateResult(false, -1, "ISD-R 逻辑通道无法打开");
        }
        try
        {
            var profiles = await es10.GetProfilesAsync(ct).ConfigureAwait(false);
            var profile = profiles?.FirstOrDefault(item => string.Equals(item.Iccid, iccid, StringComparison.Ordinal));
            if (profile is null || profile.State != 0 || profile.IsBootstrap)
                return new LogicalChannelEs10.ProfileStateResult(false, -3, "Profile 不可删除");

            var notificationsBefore = await TryListEsimNotificationsAsync(es10, ct).ConfigureAwait(false);
            var lastSequence = notificationsBefore?.Select(item => item.SequenceNumber).DefaultIfEmpty(0).Max() ?? 0;
            var freeNvramBefore = await TryGetFreeEsimNvramAsync(es10, ct).ConfigureAwait(false);
            var result = await es10.DeleteProfileAsync(iccid, ct).ConfigureAwait(false);
            if (!result.Ok) return result;

            string? warning;
            try
            {
                warning = await HandleNewDeleteNotificationAsync(es10, lastSequence, iccid, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                warning = "删除通知未处理";
            }
            catch
            {
                warning = "删除通知未处理";
            }

            long? freeNvramAfter = null;
            if (!ct.IsCancellationRequested)
            {
                try { freeNvramAfter = await ReadFreeEsimNvramWithRetryAsync(es10, attempts: 3, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            long? spaceDelta = freeNvramBefore is > 0 && freeNvramAfter is long after && after > freeNvramBefore.Value
                ? after - freeNvramBefore.Value
                : null;
            return result with { Message = "删除成功", Warning = warning, SpaceDeltaBytes = spaceDelta };
        }
        finally
        {
            await es10.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task<LogicalChannelEs10.ProfileStateResult> RenameEsimProfileAsync(
        string iccid, string nickname, CancellationToken ct = default)
    {
        var es10 = Info?.Es10;
        if (es10 is null)
            return new LogicalChannelEs10.ProfileStateResult(false, -1, "eUICC 通道未就绪");
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.EsimModification, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new LogicalChannelEs10.ProfileStateResult(false, -2, $"操作被仲裁器拒绝：{admission.ErrorCode}");
        await using var lease = admission.Lease;
        if (es10.Channel <= 0)
        {
            var opened = await es10.OpenIsdrAsync(ct: ct).ConfigureAwait(false);
            if (opened is null or <= 0)
                return new LogicalChannelEs10.ProfileStateResult(false, -1, "ISD-R 逻辑通道无法打开");
        }
        try
        {
            return await es10.SetNicknameAsync(iccid, nickname, ct).ConfigureAwait(false);
        }
        finally
        {
            await es10.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitForEsimProfileStateAsync(
        LogicalChannelEs10 es10, string iccid, int expectedState, CancellationToken ct)
    {
        const int maxAttempts = 40;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

            var opened = await es10.OpenIsdrAsync(ct: ct).ConfigureAwait(false);
            if (opened is null or <= 0)
                continue;
            try
            {
                var profiles = await es10.GetProfilesAsync(ct).ConfigureAwait(false);
                var profile = profiles?.FirstOrDefault(item =>
                    string.Equals(item.Iccid, iccid, StringComparison.Ordinal));
                if (profile?.State == expectedState)
                    return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A card reset can leave the ISD-R briefly unavailable. Retry until the state is readable.
            }
            finally
            {
                await es10.CloseAsync().ConfigureAwait(false);
            }
        }
        return false;
    }

    private static async Task<List<LogicalChannelEs10.NotificationMetadata>?> TryListEsimNotificationsAsync(
        LogicalChannelEs10 es10, CancellationToken ct)
    {
        try { return await es10.ListNotificationsAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<long?> TryGetFreeEsimNvramAsync(LogicalChannelEs10 es10, CancellationToken ct)
    {
        try { return await es10.GetFreeNvramBytesAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<long?> ReadFreeEsimNvramWithRetryAsync(
        LogicalChannelEs10 es10, int attempts, CancellationToken ct)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var bytes = await TryGetFreeEsimNvramAsync(es10, ct).ConfigureAwait(false);
            if (bytes is > 0) return bytes;
            if (attempt < attempts - 1)
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<string?> HandleNewDeleteNotificationAsync(
        LogicalChannelEs10 es10, long lastSequence, string iccid, CancellationToken ct)
    {
        LogicalChannelEs10.NotificationMetadata? notification = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var items = await TryListEsimNotificationsAsync(es10, ct).ConfigureAwait(false);
            notification = items?
                .Where(item => item.Event == 3 && item.SequenceNumber > lastSequence &&
                               string.Equals(item.Iccid, iccid, StringComparison.Ordinal))
                .OrderBy(item => item.SequenceNumber)
                .FirstOrDefault();
            if (notification is not null) break;
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        }
        if (notification is null) return "删除通知未处理";

        string? warning = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            warning = await es10.HandleNotificationAsync(notification.SequenceNumber, ct).ConfigureAwait(false);
            if (warning is null) return null;
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        }
        return warning;
    }

    private async Task<string?> ReadCurrentIccidAsync(CancellationToken ct)
    {
        var engine = Engine;
        if (engine is null) return null;
        AtResult result;
        try
        {
            result = await engine.ExecuteAsync("AT+QCCID", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        if (!result.Succeeded) return null;
        var line = result.LinesWithPrefix("+QCCID").FirstOrDefault();
        var separator = line?.IndexOf(':') ?? -1;
        return separator < 0 ? null : line![(separator + 1)..].Trim();
    }

    private void UpdateCurrentIccid(string? iccid)
    {
        Status.Iccid = iccid;
        Status.UpdatedAt = DateTime.Now;
        StatusUpdated?.Invoke(this, Status);
    }

    public Task<bool> SetCallMuteAsync(bool muted, CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.SetMuteAsync(muted, ct), ct);

    private async Task<bool> RunCallControlAsync(Func<CallService, Task<bool>> operation, CancellationToken ct)
    {
        // 捕获到局部变量：await 期间字段可能被 DisconnectAsync 置空。
        var calls = Calls;
        if (calls is null) return false;
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.CallControl, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed) return false;
        await using var lease = admission.Lease;
        return await operation(calls).ConfigureAwait(false);
    }

    internal static (ModuleOperationKind Kind, bool RawCommandIsReadOnly) ClassifyAtCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var normalized = command.Trim().ToUpperInvariant();
        if (normalized.StartsWith("AT+CFUN=", StringComparison.Ordinal))
            return (ModuleOperationKind.CfunChange, false);
        if (normalized.StartsWith("AT+CGLA=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+CSIM=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+CCHO=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+CCHC=", StringComparison.Ordinal))
            return (ModuleOperationKind.EsimModification, false);
        if (normalized == "ATA" || normalized == "ATH" || normalized.StartsWith("ATD", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+CHUP", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+VTS=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+CMUT=", StringComparison.Ordinal))
            return (ModuleOperationKind.CallControl, false);
        if (normalized.StartsWith("AT+CMGS=", StringComparison.Ordinal) || normalized.EndsWith("\u001A", StringComparison.Ordinal))
            return (ModuleOperationKind.SmsWrite, false);
        if (normalized.StartsWith("AT+CGATT=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+QNETDEVCTL=", StringComparison.Ordinal) ||
            normalized.StartsWith("AT+QCFG=\"USBNET\"", StringComparison.Ordinal))
            return (ModuleOperationKind.UsbNetworkChange, false);
        if (IsReadOnlyConsoleCommand(normalized)) return (ModuleOperationKind.ReadOnlyQuery, true);
        return (ModuleOperationKind.RawConsoleCommand, false);
    }

    private static bool IsReadOnlyConsoleCommand(string command) => command is
        "AT" or "ATI" or "AT+CGMI" or "AT+CGMM" or "AT+CGMR" or "AT+CGSN" or
        "AT+CLCC" or "AT+CPIN?" or "AT+CSQ" or "AT+CPMS?" or "AT+COPS?" or
        "AT+CREG?" or "AT+CEREG?" or "AT+CGATT?" or "AT+CFUN?" or "AT+QENG=\"SERVINGCELL\"";

    /// <summary>
    /// 主动上报泵：周期性轮询 AtEngine 的上报队列并分发给各服务。
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);

                if (Engine is null)
                {
                    continue;
                }

                var lines = Engine.DrainUnsolicited();
                foreach (var line in lines)
                {
                    UnsolicitedLine?.Invoke(this, line);

                    // 先给通话服务（RING/CLIP 需要优先处理）
                    if (Calls?.HandleUnsolicited(line) == true)
                    {
                        continue;
                    }

                    // 再给短信服务
                    Sms?.FeedLine(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 泵不因单次异常退出
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _connectLock.Dispose();
    }
}
