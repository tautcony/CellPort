using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;
using CellPort.Core.At;
using CellPort.Core.Models;

namespace CellPort.Core.Services;

/// <summary>
/// 基于 3GPP TS 27.007 逻辑通道指令（CCHO / CCHC / CGLA）的 eUICC ES10 客户端。
///
/// 为什么不用 AT+CSIM：
///   实测 QDC507 固件（QDC507GLEFM21_01.001.02.001）的 CSIM 透传对单条 APDU
///   有约 8 字节的长度上限，无法承载 SELECT ISD-R（21 字节）。
///   而 CCHO 是「模块自己」去选 ISD-R —— 21 字节 SELECT 由固件内部完成，
///   我们只需要传 32 个 hex 字符的 AID 字符串；后续 ES10 指令经 CGLA
///   在已打开的逻辑通道上收发。这正是 CellDock / lpac 采用的通道，
///   在同一模块上已验证可读 EID 与 Profile。
///
/// 协议细节（与 lpac euicc.c / driver/apdu/at.c 逐条对齐）：
///   · CCHO 响应形如 +CCHO: &lt;n&gt;，n 为通道号；
///   · CGLA 请求形如 AT+CGLA=&lt;ch&gt;,&lt;hex长度&gt;,"&lt;hex&gt;"，响应形如 +CGLA: &lt;len&gt;,"&lt;hex&gt;"；
///   · ES10 指令 = STORE DATA（80 E2），P1=0x11（中间块）/ 0x91（末块），P2=块序号，
///     CLA 混入逻辑通道号（lpac: cla = (cla &amp; 0xF0) | (channel &amp; 0x0F)）；
///   · 分块上限 es10x_mss = 120 字节（lpac 默认）；
///   · 响应 sw1=0x61 时用 GET RESPONSE（80 C0 00 00 &lt;sw2&gt;）循环取数，
///     直到 (sw1 &amp; 0xF0) == 0x90。
/// </summary>
public sealed class LogicalChannelEs10
{
    private static readonly HttpClient NotificationHttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly AtEngine _engine;
    private readonly HttpClient _notificationHttpClient;
    private int _channel;

    /// <summary>最近成功打开的 ISD-R AID。</summary>
    public string? IsdrAidHex { get; private set; }

    /// <summary>GSMA 标准 ISD-R AID（16 字节）。</summary>
    public const string StandardIsdrAidHex = "A0000005591010FFFFFFFF8900000100";

    /// <summary>与 DJOneHub 一致的 ISD-R AID 候选顺序。</summary>
    internal static readonly string[] KnownIsdrAidHexes =
    [
        "A06573746B6D65FFFF4953442D522030", // eSTK.me SE0
        "A06573746B6D65FFFF4953442D522031", // eSTK.me SE1
        StandardIsdrAidHex,
        "A0000005591010000000008900000300", // eSIM.me
        "A0000005591010000000890000000300", // eSIM.me V2
        "A0000005591010FFFFFFFF8900050500", // 5ber.eSIM
        "A0000005591010FFFFFFFF8900000177", // XeSIM
        "A000000559104C696E6B736669656C64", // LinksField
        "A0000006281010FFFFFFFF8900000100", // GlocalMe
        "A06573746B6D65FFFFFFFF4953442D52", // eSTK.me AUX
    ];

    /// <summary>ES10 STORE DATA 单块上限，取 lpac 默认值。</summary>
    private const int Es10xMss = 120;

    public LogicalChannelEs10(AtEngine engine, HttpClient? notificationHttpClient = null)
    {
        _engine = engine;
        _notificationHttpClient = notificationHttpClient ?? NotificationHttpClient;
    }

    /// <summary>当前逻辑通道号（0 = 未打开）。</summary>
    public int Channel => _channel;

    /// <summary>
    /// 探测固件是否支持逻辑通道指令组。CellDock 的做法完全一致：
    /// AT+CCHO=? 与 AT+CGLA=? 必须都能得到 OK。
    /// </summary>
    public async Task<bool> CheckSupportedAsync(CancellationToken ct = default)
    {
        var ccho = await ExecAsync("AT+CCHO=?", ct).ConfigureAwait(false);
        if (ccho is null)
        {
            return false;
        }

        var cgla = await ExecAsync("AT+CGLA=?", ct).ConfigureAwait(false);
        return cgla is not null;
    }

    /// <summary>
    /// 打开到 ISD-R 的逻辑通道。参照 lpac：先对通道 1..3 逐个尝试关闭
    /// （清掉上次进程崩溃留下的残留通道），再发 CCHO。
    /// </summary>
    /// <returns>通道号；失败返回 null。</returns>
    public async Task<int?> OpenIsdrAsync(string? aidHex = null, CancellationToken ct = default)
    {
        aidHex ??= IsdrAidHex ?? StandardIsdrAidHex;

        if (_channel > 0)
            await CloseAsync().ConfigureAwait(false);
        _channel = 0;

        for (var i = 1; i <= 3; i++)
        {
            await ExecAsync($"AT+CCHC={i}", ct).ConfigureAwait(false);
        }

        var r = await ExecAsync($@"AT+CCHO=""{aidHex}""", ct, AtEngine.LongTimeout)
            .ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var line = r.LinesWithPrefix("+CCHO").FirstOrDefault() ?? r.RawText;
        var m = Regex.Match(line, @"\+CCHO:\s*(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        var ch = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (ch is <= 0 or > 19)
        {
            return null; // CellDock 同样拒绝非法通道号
        }

        _channel = ch;
        IsdrAidHex = aidHex;
        return ch;
    }

    /// <summary>关闭当前逻辑通道。</summary>
    public async Task CloseAsync()
    {
        if (_channel > 0)
        {
            await ExecAsync($"AT+CCHC={_channel}", ct: default).ConfigureAwait(false);
            _channel = 0;
        }
    }

    /// <summary>一次 APDU 收发结果。</summary>
    /// <param name="Data">响应数据（不含 SW）。</param>
    /// <param name="Sw1">状态字高字节。</param>
    /// <param name="Sw2">状态字低字节。</param>
    public sealed record ApduExchange(IReadOnlyList<byte> Data, byte Sw1, byte Sw2);

    /// <summary>
    /// 经 CGLA 发送单条 APDU 并取回响应。响应 hex 的最后两字节为 SW。
    /// </summary>
    public async Task<ApduExchange?> TransmitAsync(byte[] apdu, CancellationToken ct = default)
    {
        if (_channel <= 0 || apdu.Length == 0)
        {
            return null;
        }

        var hex = Hex(apdu);
        var cmd = $@"AT+CGLA={_channel},{hex.Length},""{hex}""";

        var r = await ExecAsync(cmd, ct, AtEngine.LongTimeout).ConfigureAwait(false);
        if (r is null)
        {
            return null;
        }

        var line = r.LinesWithPrefix("+CGLA").FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        // +CGLA: <len>,"<hex>" —— 取第一个逗号之后的引号内内容。
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return null;
        }

        var body = line[(colon + 1)..];
        var comma = body.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var hexPart = body[(comma + 1)..].Trim().Trim('"').Trim();
        if (hexPart.Length < 4 || hexPart.Length % 2 != 0)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Unhex(hexPart);
        }
        catch
        {
            return null;
        }

        if (bytes.Length < 2)
        {
            return null;
        }

        return new ApduExchange(bytes[..^2], bytes[^2], bytes[^1]);
    }

    /// <summary>
    /// 发送一条 ES10 指令（入参为 BER-TLV 请求），返回拼接后的 BER-TLV 响应。
    /// 分块与 GET RESPONSE 循环逻辑与 lpac es10x_transmit_iter 一致。
    /// </summary>
    public async Task<byte[]?> Es10CommandAsync(byte[] berRequest, CancellationToken ct = default)
    {
        if (_channel <= 0)
        {
            return null;
        }

        var collected = new List<byte>();
        var offset = 0;
        var seq = 0;

        while (true)
        {
            var take = Math.Min(berRequest.Length - offset, Es10xMss);
            var isLast = offset + take >= berRequest.Length;
            var p1 = isLast ? (byte)0x91 : (byte)0x11;

            var apdu = new byte[5 + take];
            apdu[0] = (byte)(0x80 | (_channel & 0x0F));
            apdu[1] = 0xE2;
            apdu[2] = p1;
            apdu[3] = (byte)seq;
            apdu[4] = (byte)take;
            if (take > 0)
            {
                Array.Copy(berRequest, offset, apdu, 5, take);
            }

            var resp = await TransmitAsync(apdu, ct).ConfigureAwait(false);
            if (resp is null)
            {
                return null;
            }

            // 本块的数据收集 + 61xx 续读循环
            while (true)
            {
                for (var i = 0; i < resp.Data.Count; i++)
                {
                    collected.Add(resp.Data[i]);
                }

                if (resp.Sw1 == 0x61)
                {
                    var gr = new byte[]
                    {
                        (byte)(0x80 | (_channel & 0x0F)),
                        0xC0, 0x00, 0x00, (byte)resp.Sw2,
                    };
                    resp = await TransmitAsync(gr, ct).ConfigureAwait(false);
                    if (resp is null)
                    {
                        return null;
                    }
                    continue;
                }

                if ((resp.Sw1 & 0xF0) == 0x90)
                {
                    break; // 本块完成
                }

                return null; // 非 90xx/61xx 视为错误
            }

            offset += take;
            seq++;
            if (offset >= berRequest.Length)
            {
                break;
            }
        }

        return collected.ToArray();
    }

    /// <summary>
    /// 读取 EID。两条路径，先标准后传统：
    ///   1. ES10c GetEuiccData（BF3E 02 5C 01 5A）—— SGP.22 标准方式（lpac 同款）；
    ///   2. GET DATA（&lt;CLA|通道&gt; CA 9F 7F 00）—— SGP.02 传统方式。
    /// 实测 9eSIM 的 ISD-R 对 BF3E 返回 6A80（不支持），必须走路径 2。
    /// </summary>
    public async Task<string?> GetEidAsync(CancellationToken ct = default)
    {
        // ---- 路径 1：ES10c GetEuiccData ----
        byte[] req = [0xBF, 0x3E, 0x02, 0x5C, 0x01, 0x5A];

        var resp = await Es10CommandAsync(req, ct).ConfigureAwait(false);
        if (resp is not null)
        {
            var bf3e = BerTlv.Find(BerTlv.Parse(resp), 0xBF3E);
            if (bf3e is not null)
            {
                var tag5a = BerTlv.Find(BerTlv.Children(bf3e), 0x5A);
                if (tag5a is not null)
                {
                    return Hex(tag5a.Value.ToArray());
                }
            }
        }

        // ---- 路径 2：GET DATA (EID) ----
        var gd = new byte[]
        {
            (byte)(0x80 | (_channel & 0x0F)),
            0xCA, 0x9F, 0x7F, 0x00,
        };

        var r2 = await TransmitAsync(gd, ct).ConfigureAwait(false);
        if (r2 is null || r2.Data.Count == 0)
        {
            return null;
        }

        // 响应数据为 BER-TLV：容器 9F7F → 5A → EID；也有的卡直接给 5A。
        var nodes = BerTlv.Parse(r2.Data.ToArray(), 0, r2.Data.Count);
        byte[]? eidBytes = null;

        var container = BerTlv.Find(nodes, 0x9F7F);
        if (container is not null)
        {
            eidBytes = BerTlv.Find(BerTlv.Children(container), 0x5A)?.Value.ToArray();
        }
        eidBytes ??= BerTlv.Find(nodes, 0x5A)?.Value.ToArray();

        return eidBytes is null || eidBytes.Length == 0 ? null : Hex(eidBytes);
    }

    /// <summary>
    /// 读取 Profile 列表（ES10c GetProfilesInfo，请求必要的 Profile 字段）。
    /// 解析规则与 lpac es10c.c 一致：BF2D → A0 → 逐个 E3 → 子 tag。
    /// </summary>
    public async Task<List<EsimProfileInfo>?> GetProfilesAsync(CancellationToken ct = default)
    {
        var req = BuildProfileListRequest();

        var resp = await Es10CommandAsync(req, ct).ConfigureAwait(false);
        if (resp is null)
        {
            return null;
        }

        var bf2d = BerTlv.Find(BerTlv.Parse(resp), 0xBF2D);
        if (bf2d is null)
        {
            return null;
        }

        var a0 = BerTlv.Find(BerTlv.Children(bf2d), 0xA0);
        if (a0 is null)
        {
            return []; // 列表为空也视为成功
        }

        var profiles = new List<EsimProfileInfo>();
        foreach (var e3 in BerTlv.Children(a0))
        {
            if (e3.Tag != 0xE3)
            {
                continue;
            }

            var p = new EsimProfileInfo();
            foreach (var t in BerTlv.Children(e3))
            {
                switch (t.Tag)
                {
                    case 0x5A:
                        p.Iccid = IccidFromBcd(t.Value.ToArray());
                        break;
                    case 0x4F:
                        p.IsdpAid = Hex(t.Value.ToArray());
                        break;
                    case 0x9F70:
                        p.State = (int)BerTlv.ToLong(t.Value);
                        break;
                    case 0x90:
                        p.Nickname = DecodeProfileText(t.Value);
                        break;
                    case 0x91:
                        p.ServiceProvider = DecodeProfileText(t.Value);
                        break;
                    case 0x92:
                        p.ProfileName = DecodeProfileText(t.Value);
                        break;
                    case 0x95:
                        p.ProfileClass = (int)BerTlv.ToLong(t.Value);
                        break;
                }
            }
            profiles.Add(p);
        }

        return profiles;
    }

    public sealed record NotificationMetadata(long SequenceNumber, int Event, string? Iccid, string Address);
    public sealed record PendingNotification(NotificationMetadata Metadata, string EncodedNotification);

    /// <summary>读取 eUICC 通知列表。</summary>
    public async Task<List<NotificationMetadata>?> ListNotificationsAsync(CancellationToken ct = default)
    {
        var response = await Es10CommandAsync(BuildListNotificationsRequest(), ct).ConfigureAwait(false);
        var root = response is null ? null : BerTlv.Find(BerTlv.Parse(response), 0xBF28);
        var list = root is null ? null : BerTlv.Find(BerTlv.Children(root), 0xA0);
        if (list is null) return null;

        var notifications = new List<NotificationMetadata>();
        foreach (var node in BerTlv.Children(list))
        {
            if (node.Tag != 0xBF2F) continue;
            var metadata = ParseNotificationMetadata(node);
            if (metadata is not null) notifications.Add(metadata);
        }
        return notifications;
    }

    /// <summary>读取单条待发送通知，并按 SGP.22 格式发送至其 SM-DP+ 地址。</summary>
    public async Task<string?> HandleNotificationAsync(long sequenceNumber, CancellationToken ct = default)
    {
        var request = BuildRetrieveNotificationRequest(sequenceNumber);
        var response = await Es10CommandAsync(request, ct).ConfigureAwait(false);
        var root = response is null ? null : BerTlv.Find(BerTlv.Parse(response), 0xBF2B);
        var list = root is null ? null : BerTlv.Find(BerTlv.Children(root), 0xA0);
        var pending = list is null
            ? null
            : BerTlv.Children(list).FirstOrDefault(node => node.Tag is 0xBF37 or 0x30);
        if (pending is null)
            return "删除通知读取失败";

        var metadataNode = FindRecursive(pending, 0xBF2F);
        var metadata = metadataNode is null ? null : ParseNotificationMetadata(metadataNode);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Address) || metadata.SequenceNumber != sequenceNumber)
            return "删除通知数据无效";

        var address = metadata.Address.Trim();
        var addressUri = address.Contains("://", StringComparison.Ordinal)
            ? address
            : $"{Uri.UriSchemeHttps}://{address}";
        if (!Uri.TryCreate(addressUri, UriKind.Absolute, out var absolute) || string.IsNullOrWhiteSpace(absolute.Host))
            return "删除通知地址无效";
        var baseUri = new UriBuilder(
            Uri.UriSchemeHttps, absolute.Host, absolute.IsDefaultPort ? -1 : absolute.Port).Uri;
        var endpoint = new Uri(baseUri, "/gsma/rsp2/es9plus/handleNotification");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            pendingNotification = Convert.ToBase64String(EncodeNode(pending)),
        });

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        requestMessage.Headers.UserAgent.ParseAdd("gsma-rsp-lpad");
        requestMessage.Headers.TryAddWithoutValidation("X-Admin-Protocol", "gsma/rsp/v2.5.0");
        using var result = await _notificationHttpClient.SendAsync(
            requestMessage, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!result.IsSuccessStatusCode)
            return "删除通知发送失败";
        try
        {
            // DJOneHub treats a successful HandleNotification HTTP response as success;
            // SGP.22 does not require functionExecutionStatus in this response.
            var body = await result.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
            {
                using var _ = JsonDocument.Parse(body);
            }
            return null;
        }
        catch (JsonException)
        {
            return "删除通知响应无效";
        }
    }

    /// <summary>查询 eUICC 可用非易失存储空间。</summary>
    public async Task<long?> GetFreeNvramBytesAsync(CancellationToken ct = default)
    {
        var response = await Es10CommandAsync([0xBF, 0x22, 0x00], ct).ConfigureAwait(false);
        var root = response is null ? null : BerTlv.Find(BerTlv.Parse(response), 0xBF22);
        var resource = root is null ? null : BerTlv.Find(BerTlv.Children(root), 0xA4);
        var free = resource is null ? null : BerTlv.Find(BerTlv.Children(resource), 0x82);
        return free is null ? null : BerTlv.ToLong(free.Value);
    }

    internal static byte[] BuildListNotificationsRequest(int? eventId = null)
    {
        if (eventId is null) return [0xBF, 0x28, 0x00];
        if (eventId is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(eventId));
        var bitString = new byte[] { 0x04, (byte)(0x80 >> eventId.Value) };
        return EncodeTlv([0xBF, 0x28], EncodeTlv([0x81], bitString));
    }

    internal static byte[] BuildProfileListRequest() =>
        [0xBF, 0x2D, 0x09, 0x5C, 0x07, 0x5A, 0x9F, 0x70, 0x90, 0x91, 0x92, 0x95];

    internal static byte[] BuildRetrieveNotificationRequest(long sequenceNumber)
    {
        var sequence = EncodeTlv([0x80], EncodePositiveInteger(sequenceNumber));
        return EncodeTlv([0xBF, 0x2B], EncodeTlv([0xA0], sequence));
    }

    private static NotificationMetadata? ParseNotificationMetadata(BerTlv.Node node)
    {
        var children = BerTlv.Children(node);
        var sequence = BerTlv.Find(children, 0x80);
        var eventValue = BerTlv.Find(children, 0x81);
        var address = BerTlv.Find(children, 0x0C);
        if (sequence is null || eventValue is null || address is null)
            return null;
        var eventIndex = ParseBitStringIndex(eventValue.Value);
        if (eventIndex is null || eventIndex.Value > 3)
            return null;
        var iccid = BerTlv.Find(children, 0x5A);
        return new NotificationMetadata(
            BerTlv.ToLong(sequence.Value), eventIndex.Value,
            iccid is null ? null : IccidFromBcd(iccid.Value.ToArray()),
            Encoding.UTF8.GetString(address.Value.ToArray()));
    }

    private static int? ParseBitStringIndex(IReadOnlyList<byte> value)
    {
        if (value.Count < 2 || value[0] > 7) return null;
        int? selected = null;
        var bitCount = ((value.Count - 1) * 8) - value[0];
        for (var index = 0; index < bitCount; index++)
        {
            if ((value[(index / 8) + 1] & (0x80 >> (index % 8))) == 0) continue;
            if (selected is not null) return null;
            selected = index;
        }
        return selected;
    }

    private static BerTlv.Node? FindRecursive(BerTlv.Node node, int tag)
    {
        if (node.Tag == tag) return node;
        foreach (var child in BerTlv.Children(node))
        {
            var found = FindRecursive(child, tag);
            if (found is not null) return found;
        }
        return null;
    }

    private static byte[] EncodePositiveInteger(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)(value & 0xFF));
            value >>= 8;
        } while (value > 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return bytes.ToArray();
    }

    private static byte[] EncodeNode(BerTlv.Node node) =>
        EncodeTlv(TagBytes(node.Tag), node.Value.ToArray());

    private static byte[] TagBytes(int tag)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Insert(0, (byte)(tag & 0xFF));
            tag >>= 8;
        } while (tag > 0);
        return bytes.ToArray();
    }

    private static byte[] EncodeTlv(byte[] tag, byte[] value)
    {
        var length = value.Length < 0x80
            ? new[] { (byte)value.Length }
            : value.Length <= 0xFF
                ? new[] { (byte)0x81, (byte)value.Length }
                : [(byte)0x82, (byte)(value.Length >> 8), (byte)value.Length];
        return [.. tag, .. length, .. value];
    }

    /// <summary>ES10c 启用 / 停用 Profile 的结果。</summary>
    /// <param name="Ok">Code == 0 即成功。</param>
    /// <param name="Code">eUICC 返回的 profileOperationResult（0 成功，见 SGP.22 ErrorReason）。</param>
    /// <param name="Message">结果说明（成功 / 错误原因）。</param>
    public sealed record ProfileStateResult(
        bool Ok, long Code, string? Message, string? Warning = null, long? SpaceDeltaBytes = null);

    /// <summary>
    /// 启用 / 停用 Profile（ES10c EnableProfile `BF31` / DisableProfile `BF32`）。
    ///
    /// 请求编码与 DJOneHub AT 路径一致（refreshFlag = TRUE）：
    ///   BF31 &lt;len&gt; A0 &lt;len&gt; 5A 0A &lt;ICCID BCD&gt; 81 01 FF
    /// 响应：BF31 &lt;len&gt; 80 01 &lt;code&gt;，code=0 成功；
    ///   1 = ICCID/AID not found，2 = profile state invalid，3 = disallowed by policy，
    ///   4 = wrong profile reenabling，5 = CAT busy，127 = undefined error。
    /// </summary>
    internal async Task<ProfileStateResult> SetProfileStateAsync(
        string profileIdentifier, bool enable, CancellationToken ct = default)
    {
        if (_channel <= 0)
        {
            return new ProfileStateResult(false, -1, "ISD-R 逻辑通道未打开");
        }

        byte[] req;
        try
        {
            req = BuildProfileStateRequest(enable, profileIdentifier, refresh: true);
        }
        catch (ArgumentException ex)
        {
            return new ProfileStateResult(false, -1, $"Profile 标识格式错误：{ex.Message}");
        }

        return await ExecuteProfileOpAsync(req, enable ? 0xBF31 : 0xBF32, ct).ConfigureAwait(false);
    }

    /// <summary>发送一条 ES10c 操作指令并解析「BFxx &lt;len&gt; 80 01 &lt;code&gt;」形式的结果。</summary>
    private async Task<ProfileStateResult> ExecuteProfileOpAsync(
        byte[] request, int opTag, CancellationToken ct)
    {
        var resp = await Es10CommandAsync(request, ct).ConfigureAwait(false);
        if (resp is null)
        {
            return new ProfileStateResult(false, -1, "eUICC 无响应（STORE DATA / CGLA 失败）");
        }

        var root = BerTlv.Find(BerTlv.Parse(resp), opTag);
        if (root is null)
        {
            return new ProfileStateResult(false, -1, "响应格式异常：" + Hex(resp));
        }

        var codeNode = BerTlv.Find(BerTlv.Children(root), 0x80);
        if (codeNode is null)
        {
            return new ProfileStateResult(false, -1, "响应缺少结果字段：" + Hex(resp));
        }

        var code = BerTlv.ToLong(codeNode.Value);
        return new ProfileStateResult(code == 0, code, DescribeResultCode(code));
    }

    /// <summary>
    /// 删除 Profile（ES10c DeleteProfile `BF33`，与 lpac es10c_delete_profile 同款）。
    /// 与 Enable/Disable 的关键差别：删除请求没有 A0 包装、不带 refreshFlag ——
    ///   BF33 &lt;len&gt; 4F &lt;len&gt; &lt;ISD-P AID&gt;   （或 5A + ICCID BCD）
    /// 响应：BF33 &lt;len&gt; 80 01 &lt;code&gt;。
    /// 注意：已启用的 Profile 通常会被卡拒绝（需先停用再删除）；删除不可恢复。
    /// </summary>
    public async Task<ProfileStateResult> DeleteProfileAsync(
        string isdpAidHexOrIccid, CancellationToken ct = default)
    {
        if (_channel <= 0)
        {
            return new ProfileStateResult(false, -1, "ISD-R 逻辑通道未打开");
        }

        byte[] req;
        try
        {
            req = BuildDeleteProfileRequest(isdpAidHexOrIccid);
        }
        catch (ArgumentException ex)
        {
            return new ProfileStateResult(false, -1, ex.Message);
        }

        return await ExecuteProfileOpAsync(req, 0xBF33, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 设置 / 清除 Profile 昵称（ES10c SetNickname `BF29`，lpac es10c_set_nickname 同款）。
    /// SGP.22 规定昵称按 ICCID 定位（tag 5A，BCD 10 字节），昵称用 tag 90 承载 UTF-8（0..64 字节）：
    ///   BF29 &lt;len&gt; 5A 0A &lt;ICCID BCD&gt; 90 &lt;len&gt; &lt;utf8&gt;
    /// 传空字符串即清除昵称。响应：BF29 &lt;len&gt; 80 01 &lt;code&gt;。
    /// 昵称写入不影响 Profile 运行状态，无需重启模块。
    /// </summary>
    public async Task<ProfileStateResult> SetNicknameAsync(
        string iccid, string nickname, CancellationToken ct = default)
    {
        if (_channel <= 0)
        {
            return new ProfileStateResult(false, -1, "ISD-R 逻辑通道未打开");
        }

        byte[] req;
        try
        {
            req = BuildNicknameRequest(iccid, nickname);
        }
        catch (ArgumentException ex)
        {
            return new ProfileStateResult(false, -1, ex.Message);
        }

        return await ExecuteProfileOpAsync(req, 0xBF29, ct).ConfigureAwait(false);
    }

    // ---------- 报文编码（internal，供单元测试逐字节验证） ----------

    /// <summary>
    /// 构造 BF31（启用）/ BF32（停用）请求，与 DJOneHub AT 路径逐字节对齐：
    ///   BF31 &lt;len&gt; A0 &lt;len&gt; 5A 0A &lt;ICCID BCD&gt; 81 01 FF
    /// 也接受 32 字符 hex 的 ISD-P AID 作为备用标识。
    /// </summary>
    internal static byte[] BuildProfileStateRequest(bool enable, string id, bool refresh = true)
    {
        var ident = BuildProfileIdentifier(id);
        var choiceLen = ident.Length + 3; // ident + 81 01 00
        var req = new byte[5 + choiceLen];
        req[0] = 0xBF;
        req[1] = (byte)(enable ? 0x31 : 0x32);
        req[2] = (byte)(choiceLen + 2); // A0 TLV 总长
        req[3] = 0xA0;
        req[4] = (byte)choiceLen;
        Array.Copy(ident, 0, req, 5, ident.Length);
        var f = 5 + ident.Length;
        req[f] = 0x81;
        req[f + 1] = 0x01;
        req[f + 2] = refresh ? (byte)0xFF : (byte)0x00;
        return req;
    }

    /// <summary>
    /// 构造 BF33（删除）请求：BF33 &lt;len&gt; 5A 0A &lt;ICCID BCD&gt;，无 A0 包装、无 refreshFlag。
    /// </summary>
    internal static byte[] BuildDeleteProfileRequest(string id)
    {
        var ident = BuildProfileIdentifier(id);
        var req = new byte[3 + ident.Length];
        req[0] = 0xBF;
        req[1] = 0x33;
        req[2] = (byte)ident.Length;
        Array.Copy(ident, 0, req, 3, ident.Length);
        return req;
    }

    /// <summary>
    /// 构造 BF29（设昵称）请求：BF29 &lt;len&gt; 5A 0A &lt;ICCID BCD(10)&gt; 90 &lt;len&gt; &lt;utf8&gt;。
    /// 空昵称编码为 90 00（长度 0 即清除）。
    /// </summary>
    internal static byte[] BuildNicknameRequest(string iccid, string nickname)
    {
        if (iccid.Length is < 18 or > 20 || iccid.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("ICCID 应为 18~20 位数字");
        }
        byte[] nicknameBytes;
        try
        {
            nicknameBytes = StrictUtf8.GetBytes(nickname);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("昵称不是有效的 UTF-8 字符串", nameof(nickname), ex);
        }
        if (nicknameBytes.Length > 64)
        {
            throw new ArgumentException("昵称应为不超过 64 个 UTF-8 字节");
        }

        var bcd = IccidToBcd(iccid, 10);
        var nick = nicknameBytes;
        // 5A TLV(12) + 90 TLV(2+n)
        var req = new byte[3 + 12 + 2 + nick.Length];
        req[0] = 0xBF;
        req[1] = 0x29;
        req[2] = (byte)(14 + nick.Length);
        req[3] = 0x5A;
        req[4] = 0x0A;
        Array.Copy(bcd, 0, req, 5, 10);
        req[15] = 0x90;
        req[16] = (byte)nick.Length;
        Array.Copy(nick, 0, req, 17, nick.Length);
        return req;
    }

    /// <summary>构造 Profile 标识 TLV：32 字符 hex → 4F + AID；否则按 ICCID → 5A + BCD。</summary>
    private static byte[] BuildProfileIdentifier(string id)
    {
        if (id.Length == 32)
        {
            byte[] aid;
            try
            {
                aid = Unhex(id);
            }
            catch
            {
                throw new ArgumentException("ISD-P AID 格式错误");
            }
            if (aid.Length == 0 || aid.Length > 16)
            {
                throw new ArgumentException("ISD-P AID 长度异常");
            }

            var tlv = new byte[2 + aid.Length];
            tlv[0] = 0x4F;
            tlv[1] = (byte)aid.Length;
            Array.Copy(aid, 0, tlv, 2, aid.Length);
            return tlv;
        }

        var bcd = IccidToBcd(id, 10);
        var t = new byte[12];
        t[0] = 0x5A;
        t[1] = 0x0A;
        Array.Copy(bcd, 0, t, 2, 10);
        return t;
    }

    /// <summary>
    /// ICCID 数字串 → BCD（低 nibble 在前，奇数长度补 F 填充）。与 IccidFromBcd 互逆。
    /// </summary>
    internal static byte[] IccidToBcd(string iccid, int byteLength = 10)
    {
        if (iccid.Length > byteLength * 2 || iccid.Any(c => c is < '0' or > '9'))
        {
            throw new ArgumentException("ICCID 应为纯数字且长度不超过 " + byteLength * 2);
        }

        var nibbles = new List<byte>(byteLength * 2);
        foreach (var c in iccid)
        {
            nibbles.Add((byte)(c - '0'));
        }
        while (nibbles.Count < byteLength * 2)
        {
            nibbles.Add(0x0F);
        }

        var bytes = new byte[byteLength];
        for (var i = 0; i < byteLength; i++)
        {
            bytes[i] = (byte)(nibbles[i * 2] | (nibbles[(i * 2) + 1] << 4));
        }
        return bytes;
    }

    private static string DescribeResultCode(long code) => code switch
    {
        0 => "成功",
        1 => "ICCID 或 AID 未找到",
        2 => "Profile 状态不符合操作要求",
        3 => "操作被策略拒绝",
        4 => "当前 Profile 不允许重新启用",
        5 => "CAT 忙碌",
        127 => "未定义错误",
        _ => $"未定义错误（code={code}）",
    };

    // ---------- 工具 ----------

    private async Task<AtResult?> ExecAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            var r = await _engine.ExecuteAsync(cmd, timeout, ct).ConfigureAwait(false);
            return r.Succeeded ? r : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static byte[] Unhex(string hex)
    {
        var result = new byte[hex.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }
        return result;
    }

    internal static string DecodeProfileText(IReadOnlyList<byte> bytes) =>
        Encoding.UTF8.GetString(bytes.ToArray());

    /// <summary>
    /// ICCID 按低 nibble 在前的 BCD 存储，还原为数字串并去掉 F 填充。
    /// 映射表必须包含 A~F：ICCID 末尾的填充 nibble 是 F（15），
    /// 用 0-9 的表会越界（实测即此崩溃）。
    /// </summary>
    internal static string IccidFromBcd(byte[] bytes)
    {
        const string digits = "0123456789ABCDEF";
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(digits[b & 0x0F]);
            sb.Append(digits[(b >> 4) & 0x0F]);
        }

        var s = sb.ToString();
        var f = s.IndexOf('F');
        return f >= 0 ? s[..f] : s;
    }
}
