using System.Text.RegularExpressions;
using CellPort.Core.At;
using CellPort.Core.Models;

namespace CellPort.Core.Services;

/// <summary>
/// 查询模块与 SIM 状态。
/// </summary>
public sealed class DeviceInfoService(AtEngine engine)
{
    private string? _preferredIsdrAidHex;

    public HttpClient? EsimNotificationHttpClient { get; set; }

    /// <summary>
    /// 读取一份完整状态快照。各字段独立容错，单项失败不影响整体。
    /// </summary>
    public async Task<DeviceStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        var status = new DeviceStatus { Connected = true };

        // 型号
        var cgmm = await TryAsync("AT+CGMM", ct).ConfigureAwait(false);
        if (cgmm is { Succeeded: true })
        {
            status.Model = cgmm.FirstLine ?? status.Model;
        }

        // 固件版本
        var cgmr = await TryAsync("AT+CGMR", ct).ConfigureAwait(false);
        if (cgmr is { Succeeded: true })
        {
            status.FirmwareRevision = cgmr.FirstLine;
        }

        // IMEI
        var cgsn = await TryAsync("AT+CGSN", ct).ConfigureAwait(false);
        if (cgsn is { Succeeded: true })
        {
            status.Imei = cgsn.FirstLine;
        }

        // SIM 状态
        var cpin = await TryAsync("AT+CPIN?", ct).ConfigureAwait(false);
        if (cpin is { Succeeded: true })
        {
            status.SimReady = cpin.RawText.Contains("READY", StringComparison.OrdinalIgnoreCase);
        }

        if (status.SimReady)
        {
            var iccid = await TryAsync("AT+QCCID", ct).ConfigureAwait(false);
            if (iccid is { Succeeded: true })
            {
                status.Iccid = ExtractAfterColon(iccid.FirstLine);
            }

            var imsi = await TryAsync("AT+CIMI", ct).ConfigureAwait(false);
            if (imsi is { Succeeded: true })
            {
                status.Imsi = imsi.FirstLine;
            }

            var num = await TryAsync("AT+CNUM", ct).ConfigureAwait(false);
            status.OwnNumber = num is { Succeeded: true } ? ParseCnum(num) : null;
        }

        // 信号强度
        var csq = await TryAsync("AT+CSQ", ct).ConfigureAwait(false);
        if (csq is { Succeeded: true })
        {
            var m = Regex.Match(csq.RawText, @"\+CSQ:\s*(\d+)\s*,\s*(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var rssi))
            {
                status.SignalRssi = rssi;
            }
        }

        // 运营商
        var cops = await TryAsync("AT+COPS?", ct).ConfigureAwait(false);
        if (cops is { Succeeded: true })
        {
            var m = Regex.Match(cops.RawText, "\"([^\"]+)\"");
            if (m.Success)
            {
                status.Operator = m.Groups[1].Value;
            }
        }

        // 注册状态
        var creg = await TryAsync("AT+CREG?", ct).ConfigureAwait(false);
        status.RegistrationStatus = creg is { Succeeded: true } ? ParseReg(creg) : null;

        // 网络制式与附着（移远专有）
        var qnwinfo = await TryAsync("AT+QNWINFO", ct).ConfigureAwait(false);
        if (qnwinfo is { Succeeded: true })
        {
            var m = Regex.Match(qnwinfo.RawText, "\"([^\"]+)\"");
            if (m.Success)
            {
                status.NetworkMode = m.Groups[1].Value;
            }
        }

        var cgatt = await TryAsync("AT+CGATT?", ct).ConfigureAwait(false);
        if (cgatt is { Succeeded: true })
        {
            var m = Regex.Match(cgatt.RawText, @"\+CGATT:\s*(\d)");
            if (m.Success)
            {
                status.DataAttached = m.Groups[1].Value == "1";
            }
        }

        status.UpdatedAt = DateTime.Now;
        return status;
    }

    /// <summary>GSMA SGP.22 的 ISD-R 应用 AID（16 字节）。</summary>
    public const string IsdrAid = "A0000005591010FFFFFFFF8900000100";

    /// <summary>
    /// 探测成功后保留下来的逻辑通道「客户端」（无状态对象），供后续读取 Profile 等操作复用。
    /// 仅当探测结论为 Ready 时非空。
    ///
    /// 注意：探测结束时会关闭底层逻辑通道（Channel 归零），后续每次操作按需重新 CCHO。
    /// 这是刻意的：切换 Profile 会触发 UICC refresh，跨操作保留通道号会拿到失效通道
    /// （表现为 CGLA 返回 6A82/6D00），重新打开只多一次 CCHO/CCHC 往返，代价远低于排障成本。
    /// </summary>
    public LogicalChannelEs10? Es10 { get; private set; }

    /// <summary>eUICC 探测的完整结果。</summary>
    /// <param name="Capability">结论。</param>
    /// <param name="Eid">EID（仅 Ready 时有值）。</param>
    /// <param name="Profiles">Profile 列表（仅 Ready 时有值，可能为空列表）。</param>
    /// <param name="Detail">供界面/日志展示的诊断细节。</param>
    public sealed record EuiccProbeResult(
        EuiccCapability Capability,
        string? Eid,
        IReadOnlyList<EsimProfileInfo> Profiles,
        string? Detail);

    /// <summary>
    /// 探测本模块读取 eUICC 的能力，成功时顺带读出 EID 与 Profile 列表。
    ///
    /// 通道选择（重要）：优先走 3GPP 逻辑通道指令（CCHO / CGLA）——
    /// CCHO 由模块固件内部完成 ISD-R 选择，完全避开本固件 AT+CSIM 透传的
    /// 约 8 字节长度上限。这正是 CellDock / lpac 在同一模块上验证可用的路径。
    /// 仅当固件不支持 CCHO/CGLA 时，才回退到 CSIM 路径做进一步诊断。
    /// </summary>
    public async Task<EuiccProbeResult> ProbeEuiccAsync(CancellationToken ct = default)
    {
        // ---------- 路径一：逻辑通道（CCHO / CGLA）----------
        var es10 = new LogicalChannelEs10(engine, EsimNotificationHttpClient);
        Es10 = null;

        bool supported;
        try
        {
            supported = await es10.CheckSupportedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            supported = false;
        }

        if (supported)
        {
            var aidCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_preferredIsdrAidHex))
                aidCandidates.Add(_preferredIsdrAidHex);
            aidCandidates.AddRange(LogicalChannelEs10.KnownIsdrAidHexes.Where(aid =>
                !string.Equals(aid, _preferredIsdrAidHex, StringComparison.OrdinalIgnoreCase)));

            foreach (var aid in aidCandidates)
            {
                var channel = await es10.OpenIsdrAsync(aid, ct).ConfigureAwait(false);
                if (channel is not int ch)
                    continue;

                try
                {
                    string? eid;
                    try { eid = await es10.GetEidAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { throw; }
                    catch { eid = null; }

                    List<EsimProfileInfo> profiles = [];
                    var profilesOk = false;
                    try
                    {
                        profiles = await es10.GetProfilesAsync(ct).ConfigureAwait(false) ?? [];
                        profilesOk = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Profile 读取失败不算致命
                    }

                    // 判据：EID 或 Profile 任一可读即认为 eUICC 可用。
                    // 实测部分卡（如 9eSIM）不支持 ES10c GetEuiccData，但 Profile 可读。
                    if (!string.IsNullOrWhiteSpace(eid) || profilesOk)
                    {
                        _preferredIsdrAidHex = aid;
                        Es10 = es10;
                        return new EuiccProbeResult(
                            EuiccCapability.Ready, eid, profiles, $"逻辑通道 {ch}");
                    }
                }
                finally
                {
                    // 成功/失败都关闭通道：客户端实例已保留在 Es10，通道号按需重开，
                    // 避免跨操作持有可能因 UICC refresh 失效的逻辑通道（见 Es10 注释）。
                    await es10.CloseAsync().ConfigureAwait(false);
                }
            }

            return new EuiccProbeResult(
                EuiccCapability.IsdrNotFound, null, [],
                "未找到可读取的 ISD-R 应用。");
        }

        // ---------- 路径二：CSIM 诊断（仅在固件不支持逻辑通道时）----------
        var capability = await ProbeViaCsimAsync(ct).ConfigureAwait(false);
        return new EuiccProbeResult(capability, null, [],
            "固件不支持 CCHO/CGLA 逻辑通道指令，已回退 CSIM 诊断。");
    }

    /// <summary>
    /// 旧版 CSIM 路径的探测与诊断（仅在 CCHO/CGLA 不可用时被调用）。
    ///
    /// 实测限制（QDC507 固件 QDC507GLEFM21_01.001.02.001）：
    ///   AT+CSIM 透传通道对单条 APDU 的长度上限约为 16 个十六进制字符（8 字节），
    ///   实测 7 字节 → 9000、8 字节 → 可执行、9 字节及以上 → 无任何响应。
    ///   SELECT ISD-R 至少需要 10 字节，必然超出限制。
    /// </summary>
    private async Task<EuiccCapability> ProbeViaCsimAsync(CancellationToken ct)
    {
        // 1) 通道基线：SELECT MF（7 字节，实测可用）
        var mf = await TryAsync("AT+CSIM=14,\"00A40000023F00\"", ct).ConfigureAwait(false);
        if (mf is not { Succeeded: true } || !mf.RawText.Contains("9000", StringComparison.OrdinalIgnoreCase))
        {
            return EuiccCapability.ChannelUnavailable;
        }

        // 2) 长度探针：8 字节（实测可执行）与 10 字节（实测沉默）
        //    用 SELECT MF 后补零的方式构造，语义上无害。
        var len8 = await TryAsync("AT+CSIM=16,\"00A40000023F0000\"", ct).ConfigureAwait(false);
        var len10 = await TryAsync("AT+CSIM=20,\"00A40000023F00000000\"", ct).ConfigureAwait(false);

        var ack8 = len8 is { Succeeded: true } && len8.Lines.Count > 0;
        var ack10 = len10 is { Succeeded: true } && len10.Lines.Count > 0;

        if (ack8 && !ack10)
        {
            // 通道能干活，但 8 字节以上被截断 —— ISD-R 的 16 字节 AID 不可能送达。
            return EuiccCapability.ApduLengthLimited;
        }

        // 3) 通道无长度问题，尝试真正的 SELECT ISD-R
        var apdu = "00A4040010" + IsdrAid;
        var sel = await TryAsync($"AT+CSIM={apdu.Length},\"{apdu}\"", ct, AtEngine.LongTimeout)
            .ConfigureAwait(false);

        if (sel is { Succeeded: true } && sel.RawText.Contains("9000", StringComparison.OrdinalIgnoreCase))
        {
            return EuiccCapability.Ready;
        }

        return EuiccCapability.IsdrNotFound;
    }

    /// <summary>
    /// 查询单条状态指令。AT 层失败仍返回原始结果，便于调用方按完成状态处理；
    /// 传输异常才返回 null。日志区分「模块拒绝」与「无响应」。
    /// </summary>
    private async Task<AtResult?> TryAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var r = await engine.ExecuteAsync(cmd, timeout, ct).ConfigureAwait(false);
            // ERROR 常见于「该固件不支持此指令」，降为 Info；超时/传输失败才是真问题。
            if (!r.Succeeded && r.Completion is AtCompletionStatus.Error)
            {
                AppLog.Info($"状态查询不支持：{cmd} → {r.Completion}");
            }
            else if (!r.Succeeded)
            {
                AppLog.Warn($"状态查询失败：{cmd} → {r.Completion}");
            }
            return r;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"状态查询异常：{cmd} → {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    private static string? ExtractAfterColon(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
        var idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim().Trim('"') : line.Trim().Trim('"');
    }

    private static string? ParseCnum(AtResult? result)
    {
        if (result is null)
        {
            return null;
        }

        // 常见返回共三种形态：
        //   +CNUM: "name","+8613800138000",145     ← 有名字
        //   +CNUM: ,"+8613800138000",145             ← 无名字（本模块实测）
        //   +CNUM: "+8613800138000",145              ← 仅号码
        // 统一策略：在该行内找出所有带引号的字段，取其中「看起来像号码」的那个。
        var line = result.LinesWithPrefix("+CNUM").FirstOrDefault()
                   ?? result.RawText;

        var quoted = Regex.Matches(line, "\"([^\"]+)\"");
        foreach (var q in quoted.Cast<Match>())
        {
            var value = q.Groups[1].Value;
            // 号码特征：含 + 或全数字，且长度 >= 5
            if (value.Length >= 5 &&
                (value.StartsWith('+') || value.All(char.IsDigit)))
            {
                return value;
            }
        }

        // 退化：匹配 ,"xxx",145 形式（无引号号码）
        var m = Regex.Match(line, @",\s*([+]?\d{5,})\s*,\s*\d+");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ParseReg(AtResult? result)
    {
        if (result is null)
        {
            return null;
        }
        var m = Regex.Match(result.RawText, @"\+CREG:\s*\d\s*,\s*(\d)");
        if (!m.Success)
        {
            return null;
        }
        return m.Groups[1].Value switch
        {
            "0" => "未注册",
            "1" => "已注册（本地）",
            "2" => "搜索中",
            "3" => "注册被拒",
            "4" => "未知",
            "5" => "已注册（漫游）",
            _ => null,
        };
    }
}
