using CellPort.Core.Transport;
using CellPort.Transport.Serial;

namespace CellPort.Platform.Windows;

/// <summary>保留现有 Windows USB 优先和串口自动发现实现。</summary>
public sealed class WindowsAtChannelProvider : IAtChannelProvider
{
    public StringComparer PortNameComparer => StringComparer.OrdinalIgnoreCase;

    public async Task<IAtChannel?> TryOpenUsbAsync(CancellationToken ct = default) =>
        await UsbAtChannel.TryOpenAsync(ct).ConfigureAwait(false);

    public IAtChannel CreateSerial(string portName) => new SerialAtChannel(portName);

    /// <summary>
    /// 自动探测可能承载 AT 通道的串口。
    /// 
    /// 实测环境（大疆一代 QDC507 + Quectel 驱动，本机 COM4~COM9）：
    ///   · COM4 = DM Port（诊断口）  → 可打开，但不回 AT
    ///   · COM5 = NMEA Port          → 可打开，但不回 AT
    ///   · COM6 = AT Port            → 回复 "AT\r\r\nOK\r\n"
    ///   · COM7 = 同一 AT 口的另一实例 → 同样回 OK（热插拔后会在这两个号之间漂移）
    ///   · COM8/COM9 = 蓝牙虚拟串口   → 与模块无关，应排除
    ///
    /// 因此策略为两段式：
    ///   1. 「已知端口」先行：把上次成功的口放在最前面，单个探测（约 50ms）即可命中，
    ///      这是启动秒连的关键路径；
    ///   2. 未命中时才做全量并行探测：对系统串口并行发 AT，谁先回 OK 用谁。
    ///      蓝牙口（BTHENUM / 描述含「蓝牙」「Bluetooth」）直接剔除，避免无谓等待。
    /// </summary>
    /// <param name="knownPorts">
    /// 历史成功端口，按优先级排列（最新的在前）。可为空。
    /// </param>
    public async Task<string?> DetectSerialAsync(
        IReadOnlyList<string> knownPorts, CancellationToken ct)
    {
        // 异步枚举：串口枚举在部分机器上会阻塞数秒，不能占用调用线程。
        var candidates = await SerialAtChannel.EnumerateCandidatesAsync(ct: ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return null;
        }

        // ---- 第 1 段：已知端口优先（秒连路径）----
        foreach (var known in knownPorts)
        {
            if (ct.IsCancellationRequested)
            {
                return null;
            }

            var exists = candidates.Any(c =>
                string.Equals(c.PortName, known, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                continue;
            }

            var hit = await ProbePortAsync(known, ct).ConfigureAwait(false);
            if (hit is not null)
            {
                return hit;
            }
        }

        // ---- 第 2 段：全量并行探测 ----
        // 剔除明显无关的端口：蓝牙虚拟串口、以及没有对应模块 PnP 实例的可疑端口。
        var suspect = new HashSet<string>(knownPorts, StringComparer.OrdinalIgnoreCase);
        var portMap = await GetPortFriendlyMapAsync(ct).ConfigureAwait(false);
        var candidatesToTry = candidates
            .Where(c => !IsBluetoothPort(c.PortName, portMap))
            .Where(c => !suspect.Contains(c.PortName))
            .ToList();

        if (candidatesToTry.Count == 0)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));

        var tasks = candidatesToTry
            .Select(c => ProbePortAsync(c.PortName, cts.Token))
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);

            try
            {
                var port = await finished.ConfigureAwait(false);
                if (port is not null)
                {
                    // 取消并等待同批探测收尾，再把命中端口交给真实连接流程。
                    // 只依赖“自然结束”会让同一设备的别名端口短暂占用同一接口。
                    await cts.CancelAsync().ConfigureAwait(false);
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                    catch { /* 每个候选独立失败；命中结果仍有效。 */ }
                    return port;
                }
            }
            catch
            {
                // 该端口探测失败，继续等下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 判断某端口是否为蓝牙虚拟串口。这类端口由系统常驻，与模块无关，
    /// 对其发 AT 必然超时，属于纯粹的浪费。
    /// 判定依据：Win32_PnPEntity 的 DeviceID 以 BTHENUM 开头。
    /// 为避免每次连接都跑 WMI，结果带 30 秒缓存。
    /// </summary>
    private static bool IsBluetoothPort(string portName, IReadOnlyDictionary<string, string> portMap)
    {
        return portMap.TryGetValue(portName, out var id) &&
               id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? _portMapCache;
    private static DateTime _portMapCachedAt = DateTime.MinValue;
    private static readonly SemaphoreSlim PortMapGate = new(1, 1);

    /// <summary>
    /// 建立「COM 口名 → PnP DeviceID」映射，用于识别端口所属设备类型。
    /// 查询带超时与缓存；失败时返回空表（调用方按「非蓝牙」处理，不影响主流程）。
    /// WMI 查询本身是同步的，这里放到线程池并异步等待，避免阻塞界面线程。
    /// </summary>
    private static async Task<Dictionary<string, string>> GetPortFriendlyMapAsync(CancellationToken ct)
    {
        if (_portMapCache is { } cached &&
            DateTime.UtcNow - _portMapCachedAt < TimeSpan.FromSeconds(30))
        {
            return cached;
        }

        await PortMapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_portMapCache is { } fresh &&
                DateTime.UtcNow - _portMapCachedAt < TimeSpan.FromSeconds(30))
            {
                return fresh;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                map = await Task.Run(QueryPortMap, ct)
                    .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // WMI 不可用或超时：退化为空表
            }

            _portMapCache = map;
            _portMapCachedAt = DateTime.UtcNow;
            return map;
        }
        finally
        {
            PortMapGate.Release();
        }
    }

    private static Dictionary<string, string> QueryPortMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (var o in searcher.Get())
        {
            var name = o["Name"]?.ToString();
            var id = o["DeviceID"]?.ToString();
            var port = ExtractPortName(name);
            if (port is not null && id is not null)
            {
                map[port] = id;
            }
        }
        return map;
    }

    /// <summary>从「Quectel USB AT Port (COM6)」这类友好名中取出 COM6。</summary>
    private static string? ExtractPortName(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName))
        {
            return null;
        }

        var open = friendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return null;
        }

        var close = friendlyName.IndexOf(')', open);
        if (close < 0)
        {
            return null;
        }

        return friendlyName.Substring(open + 1, close - open - 1).Trim();
    }

    /// <summary>
    /// 探测单个串口是否为 AT 通道。
    /// </summary>
    private static async Task<string?> ProbePortAsync(string portName, CancellationToken ct)
    {
        try
        {
            var ch = new SerialAtChannel(portName);
            await using (ch)
            {
                // OpenAsync 内部已带超时保护，不可用端口会快速抛出。
                await ch.OpenAsync(ct).ConfigureAwait(false);

                // 发送探测指令；部分固件需要两次往返才回 OK
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    await ch.WriteAsync("AT\r"u8.ToArray(), ct).ConfigureAwait(false);

                    var buf = new byte[256];
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var sb = new System.Text.StringBuilder();

                    while (sw.ElapsedMilliseconds < 800 && !ct.IsCancellationRequested)
                    {
                        var read = await ch.ReadAsync(buf, TimeSpan.FromMilliseconds(200), ct)
                            .ConfigureAwait(false);
                        if (read <= 0)
                        {
                            continue;
                        }
                        sb.Append(System.Text.Encoding.ASCII.GetString(buf, 0, read));

                        if (sb.ToString().Contains("OK", StringComparison.OrdinalIgnoreCase))
                        {
                            return portName;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 整体超时，交由调用方处理
        }
        catch
        {
            // 端口不可用（被占用、不存在、打开超时等），跳过
        }
        return null;
    }

}
