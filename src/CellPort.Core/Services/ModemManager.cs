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

    private AtEngine? _engine;
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
    public bool IsConnected => _engine?.Channel.IsOpen == true;

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
    public string ChannelDescription => _engine?.Channel.Description ?? "未连接";

    /// <summary>
    /// 当前 AT 引擎。供诊断工具直接构造通道级客户端（如 LogicalChannelEs10）；
    /// 未连接时为 null。
    /// </summary>
    public AtEngine? Engine => _engine;

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

        // 1) 优先尝试 USB 直控（WinUSB）
        //    若 MI_02 尚未绑定 WinUSB，会在枚举阶段就返回 null（很快）。
        //    这里额外加超时，防止个别驱动状态下 SetupAPI 调用异常拖慢连接。
        Trace("[1/5] 尝试 USB 直控（WinUSB）…");
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
                    Trace("      USB 直控不可用（MI_02 未绑定 WinUSB），转串口。");
                }
            }
            else
            {
                Trace("      USB 探测超时，转串口。");
            }
        }
        catch (Exception ex)
        {
            Trace($"      USB 探测异常：{ex.GetType().Name}，转串口。");
        }

        // 2) 回退到串口
        if (channel is null)
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
                var detectTask = _channels.DetectSerialAsync(_portHints, ct);
                var done2 = await Task.WhenAny(detectTask, Task.Delay(TimeSpan.FromSeconds(20), ct))
                    .ConfigureAwait(false);
                port = done2 == detectTask
                    ? await detectTask.ConfigureAwait(false)
                    : null;
                Trace(port is null ? "      未找到 AT 串口。" : $"      找到 AT 串口：{port}");
            }

            if (port is not null)
            {
                channel = _channels.CreateSerial(port);

                // 记住成功端口：进程内提到列表首位，并通知宿主持久化。
                _portHints.RemoveAll(p =>
                    _channels.PortNameComparer.Equals(p, port));
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
        }

        if (channel is null)
        {
            Trace("[X] 无可用通道。");
            return false;
        }

        Trace($"[3/5] 打开通道：{channel.Description}");
        try
        {
            await channel.OpenAsync(ct).ConfigureAwait(false);
            Trace("      通道已打开。");
        }
        catch (Exception ex)
        {
            Trace($"      打开失败：{ex.GetType().Name} {ex.Message}");
            await channel.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        var engine = new AtEngine(channel);
        engine.IoTrace += (_, msg) => Trace($"      IO {msg}");

        // 3) 探测 AT 通道是否真的可用
        Trace("[4/5] 发送 AT 探测…");
        var probe = await engine.ExecuteAsync("AT", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            Trace("      AT 无响应。");
            await engine.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        Trace("      AT 响应正常。");

        _engine = engine;
        CallTracker.BeginModuleSession();

        Sms = new SmsService(engine);
        Calls = new CallService(engine, CallTracker);
        Info = new DeviceInfoService(engine);
        Cusd = new CusdService(engine);
        Phonebook = new PhonebookService(engine);

        await Sms.InitializeAsync(ct).ConfigureAwait(false);
        await Calls.InitializeAsync(ct).ConfigureAwait(false);

        // 4) 启动主动上报泵
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token));

        ConnectionChanged?.Invoke(this, true);

        // 5) 首次状态读取
        await RefreshStatusAsync(ct).ConfigureAwait(false);

        // 6) 同步已存短信
        try
        {
            await Sms.SyncStoredAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 同步失败不影响连接
        }

        return true;
    }

    /// <summary>断开连接。</summary>
    public async Task DisconnectAsync()
    {
        CallTracker.MarkModuleDisconnected();

        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }

        Sms = null;
        Calls = null;
        Info = null;
        Status = new DeviceStatus();

        ConnectionChanged?.Invoke(this, false);
    }

    /// <summary>刷新状态。</summary>
    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (Info is null)
        {
            return;
        }
        try
        {
            Status = await Info.ReadStatusAsync(ct).ConfigureAwait(false);
            StatusUpdated?.Invoke(this, Status);
        }
        catch
        {
            // 刷新失败保留上次快照
        }
    }

    /// <summary>执行任意 AT 指令（供控制台使用）。</summary>
    public async Task<AtResult> ExecuteAtAsync(string command, CancellationToken ct = default)
    {
        if (_engine is null)
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
        return await _engine.ExecuteAsync(command, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
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
        if (Sms is null) return false;
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.SmsWrite, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed) return false;
        await using var lease = admission.Lease;
        return await Sms.SendAsync(number, body, ct).ConfigureAwait(false);
    }

    public async Task<CusdService.CusdResult> SendUssdAsync(string code, CancellationToken ct = default)
    {
        if (Cusd is null) return new CusdService.CusdResult(false, null, "模块未连接");
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.RawConsoleCommand, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed)
            return new CusdService.CusdResult(false, null, $"操作被仲裁器拒绝：{admission.ErrorCode}");
        await using var lease = admission.Lease;
        return await Cusd.SendAsync(code, ct).ConfigureAwait(false);
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
        string isdpAidHex, bool enable, CancellationToken ct = default)
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
            if (opened is null || opened <= 0)
                return new LogicalChannelEs10.ProfileStateResult(false, -1, "ISD-R 逻辑通道无法打开");
        }
        return await es10.SetProfileStateAsync(isdpAidHex, enable, ct).ConfigureAwait(false);
    }

    public Task<bool> SetCallMuteAsync(bool muted, CancellationToken ct = default) =>
        RunCallControlAsync(calls => calls.SetMuteAsync(muted, ct), ct);

    private async Task<bool> RunCallControlAsync(Func<CallService, Task<bool>> operation, CancellationToken ct)
    {
        if (Calls is null) return false;
        var admission = await TryAcquireOperationAsync(ModuleOperationKind.CallControl, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!admission.Allowed) return false;
        await using var lease = admission.Lease;
        return await operation(Calls).ConfigureAwait(false);
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

                if (_engine is null)
                {
                    continue;
                }

                var lines = _engine.DrainUnsolicited();
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
