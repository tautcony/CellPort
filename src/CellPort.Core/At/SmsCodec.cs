using System.Text;

namespace CellPort.Core.At;

/// <summary>
/// GSM 7-bit 与 UCS2 编解码工具，以及 SMS-SUBMIT PDU 的构造（含长短信分段）。
/// 规范依据：3GPP TS 23.038（字母表）与 TS 23.040（PDU 结构）。
/// </summary>
public static class SmsCodec
{
    /// <summary>单条短信可承载的最大 GSM 7-bit septet 数（140 字节）。</summary>
    public const int Gsm7SingleSeptets = 160;

    /// <summary>带 6 字节 UDH 时单段可承载的最大 septet 数：⌊(140×8−49)/7⌋ = 153。</summary>
    public const int Gsm7ConcatSeptets = 153;

    /// <summary>单条短信可承载的最大 UCS2 字节数（140 字节 = 70 个 BMP 字符）。</summary>
    public const int Ucs2SingleBytes = 140;

    /// <summary>带 6 字节 UDH 时单段可承载的最大 UCS2 字节数（140 − 6 = 134）。</summary>
    public const int Ucs2ConcatBytes = 134;

    /// <summary>8-bit 参考号拼接头（UDHL + IEI 0x00 + 长度 3 + ref/total/seq）的字节数。</summary>
    public const int ConcatUdhOctets = 6;

    /// <summary>
    /// 拼接头占用的 septet 数：6 字节 = 48 bit，向上取整到 septet 边界 = 7。
    /// 发送侧按该数量预留 UDL，接收侧据此定位正文起始 septet。
    /// </summary>
    public static int ConcatUdhSeptets => (ConcatUdhOctets * 8 + 6) / 7;

    /// <summary>
    /// 判断文本是否无法用 GSM 7-bit 表示（例如中文、全角符号、箭头等）。
    /// 扩展表字符（[ ] { } ~ ^ | \ €）可以编码，不需要 UCS2。
    /// </summary>
    public static bool RequiresUcs2(string text) => text.Any(c => !CanEncodeGsm7(c));

    /// <summary>
    /// 把文本编码为 AT 指令可用的十六进制正文串。
    /// 中文走 UCS2（UTF-16BE），GSM 7-bit 可表示的文本走 7-bit 打包。
    /// </summary>
    public static (string Hex, string Dcs) EncodeForText(string text, bool forceUcs2 = false)
    {
        if (forceUcs2 || RequiresUcs2(text))
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            return (Convert.ToHexString(bytes), "08");
        }

        var gsm = EncodeGsm7Bit(text);
        return (Convert.ToHexString(gsm), "00");
    }

    /// <summary>
    /// GSM 03.38 的 7-bit 打包编码（用于纯 ASCII 短信的紧凑传输）。
    /// </summary>
    /// <exception cref="ArgumentException">文本包含 GSM 7-bit 字符集之外的字符。</exception>
    public static byte[] EncodeGsm7Bit(string text) => PackSeptets([], ToSeptets(text));

    /// <summary>
    /// 把文本编码为 septet 序列。扩展表字符编码为「ESC + 码点」两个 septet。
    /// </summary>
    /// <exception cref="ArgumentException">文本包含 GSM 7-bit 字符集之外的字符。</exception>
    public static List<byte> ToSeptets(string text)
    {
        var septets = new List<byte>(text.Length);
        foreach (var c in text)
        {
            if (Gsm7ExtensionLookup.TryGetValue(c, out var extension))
            {
                septets.Add(0x1B);
                septets.Add(extension);
            }
            else if (Gsm7Lookup.TryGetValue(c, out var code))
            {
                septets.Add(code);
            }
            else
            {
                throw new ArgumentException(
                    $"字符 U+{(int)c:X4} 不在 GSM 7-bit 字符集中，应改用 UCS2。", nameof(text));
            }
        }

        return septets;
    }

    /// <summary>
    /// 按 7-bit 打包 septet 序列；<paramref name="header"/> 为 UDH 原始字节，
    /// 会先按字节写入并补齐到 septet 边界（6 字节 UDH 补 1 位）。
    /// </summary>
    public static byte[] PackSeptets(ReadOnlySpan<byte> header, IReadOnlyList<byte> septets)
    {
        var output = new List<byte>(septets.Count + header.Length + 1);
        var accumulator = 0;
        var bits = 0;

        void Push(int value, int width)
        {
            accumulator |= (value & ((1 << width) - 1)) << bits;
            bits += width;
            while (bits >= 8)
            {
                output.Add((byte)(accumulator & 0xFF));
                accumulator >>= 8;
                bits -= 8;
            }
        }

        foreach (var b in header)
        {
            Push(b, 8);
        }

        if (header.Length > 0)
        {
            Push(0, (7 - header.Length * 8 % 7) % 7);
        }

        foreach (var s in septets)
        {
            Push(s, 7);
        }

        if (bits > 0)
        {
            output.Add((byte)(accumulator & 0xFF));
        }

        return [.. output];
    }

    /// <summary>
    /// 解码 GSM 7-bit 打包数据为文本。
    /// </summary>
    /// <param name="data">打包后的字节流（含 UDH 时应传入完整 UD，含 UDHL 字节）。</param>
    /// <param name="septetCount">正文 septet 数；≤0 时按数据长度推断。</param>
    /// <param name="startSeptet">正文起始 septet 序号（含 UDH 时为 UDH 占用的 septet 数）。</param>
    public static string DecodeGsm7Bit(ReadOnlySpan<byte> data, int septetCount = -1, int startSeptet = 0)
    {
        var remaining = septetCount > 0 ? septetCount : Math.Max(0, data.Length * 8 / 7 - startSeptet);
        var sb = new StringBuilder(remaining);
        var bitPos = (long)Math.Max(0, startSeptet) * 7;
        var totalBits = (long)data.Length * 8;

        while (remaining > 0 && bitPos + 7 <= totalBits)
        {
            var value = ReadSeptet(data, bitPos);
            bitPos += 7;
            remaining--;

            if (value == 0x1B)
            {
                if (remaining <= 0 || bitPos + 7 > totalBits)
                {
                    break;
                }

                var extension = ReadSeptet(data, bitPos);
                bitPos += 7;
                remaining--;
                sb.Append(Gsm7ExtensionReverseLookup.GetValueOrDefault(extension, ' '));
                continue;
            }

            sb.Append(ReverseGsm7Lookup.GetValueOrDefault(value, '?'));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 解码 UCS2 十六进制正文。
    /// </summary>
    public static string DecodeUcs2(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        return Encoding.BigEndianUnicode.GetString(bytes);
    }

    /// <summary>
    /// 把号码格式化为 PDU 的 BCD 半字节表示。
    /// 注意：3GPP TS 23.040 §9.1.2.5 的 TP-DA 号码本身不含任何前缀，
    /// 「国际格式」只由独立的 TOA 字节（0x91）表达。
    /// </summary>
    public static string EncodeNumberBcd(string number, bool international)
    {
        ArgumentNullException.ThrowIfNull(number);
        _ = international; // TOA 由调用方单独写入；保留参数以维持调用点语义清晰。

        var s = NormalizePhoneNumber(number);
        if (s.StartsWith('+'))
        {
            s = s[1..];
        }
        if (s.Length == 0)
        {
            throw new ArgumentException("号码不含有效数字。", nameof(number));
        }

        if (s.Any(c => !(c is >= '0' and <= '9' or '*' or '#')))
        {
            throw new ArgumentException("号码只能包含 ASCII 数字、* 或 #。", nameof(number));
        }

        if (s.Length % 2 != 0)
        {
            s += "F";
        }

        var swapped = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i += 2)
        {
            swapped.Append(s[i + 1]).Append(s[i]);
        }
        return swapped.ToString();
    }

    /// <summary>
    /// 构造一条或多条 SMS-SUBMIT PDU（SMSC 长度置 0，使用模块默认短信中心）。
    /// 超出单条上限时按 TS 23.040 的 8-bit 参考号拼接头分段。
    /// </summary>
    /// <param name="number">目标号码，可带 + 前缀。</param>
    /// <param name="body">正文。</param>
    internal static IReadOnlyList<string> BuildSubmitPdus(string number, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var trimmed = NormalizePhoneNumber(number);
        var international = trimmed.StartsWith('+');
        var digits = international ? trimmed[1..] : trimmed;
        if (digits.Length == 0)
        {
            throw new ArgumentException("号码不含有效数字。", nameof(number));
        }

        if (RequiresUcs2(body))
        {
            return BuildUcs2Body(digits, international, body);
        }

        return BuildGsm7Body(digits, international, body);
    }

    /// <summary>
    /// 规范化可显示格式的短信号码。只移除常见分隔符；字母、Unicode 数字以及
    /// 非首位/重复的 + 一律拒绝，避免把输入静默改成另一个收件号码。
    /// </summary>
    internal static string NormalizePhoneNumber(string number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);

        var normalized = new StringBuilder(number.Length);
        var hasAddressContent = false;
        var hasPlus = false;
        foreach (var c in number.Trim())
        {
            if (c is >= '0' and <= '9' or '*' or '#')
            {
                normalized.Append(c);
                hasAddressContent = true;
                continue;
            }

            if (c == '+' && !hasPlus && !hasAddressContent && normalized.Length == 0)
            {
                normalized.Append(c);
                hasPlus = true;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '-' or '(' or ')' or '.')
            {
                continue;
            }

            throw new ArgumentException($"号码包含不支持的字符 U+{(int)c:X4}。", nameof(number));
        }

        if (!hasAddressContent)
        {
            throw new ArgumentException("号码不含有效地址字符。", nameof(number));
        }

        if (normalized.Length > 255)
        {
            throw new ArgumentException("号码长度超过 SMS 地址字段上限。", nameof(number));
        }

        return normalized.ToString();
    }

    private static IReadOnlyList<string> BuildGsm7Body(string digits, bool international, string body)
    {
        var septets = ToSeptets(body);

        if (septets.Count <= Gsm7SingleSeptets)
        {
            var ud = PackSeptets([], septets);
            return [BuildPdu(digits, international, "00", ud, septets.Count, hasUdh: false)];
        }

        var parts = SplitSeptets(septets, Gsm7ConcatSeptets);
        var reference = Random.Shared.Next(1, 256);
        var pdus = new List<string>(parts.Count);

        for (var i = 0; i < parts.Count; i++)
        {
            var udh = BuildConcatUdh(reference, parts.Count, i + 1);
            var ud = PackSeptets(udh, parts[i]);
            var udl = ConcatUdhSeptets + parts[i].Count;
            pdus.Add(BuildPdu(digits, international, "00", ud, udl, hasUdh: true));
        }

        return pdus;
    }

    private static IReadOnlyList<string> BuildUcs2Body(string digits, bool international, string body)
    {
        // 先按单条上限判断：70 个 BMP 字符（140 字节）仍应作为单条发送。
        var single = ChunkUcs2(body, Ucs2SingleBytes);
        if (single.Count == 0)
        {
            throw new ArgumentException("正文为空。", nameof(body));
        }

        if (single.Count == 1)
        {
            return [BuildPdu(digits, international, "08", single[0], single[0].Length, hasUdh: false)];
        }

        var chunks = ChunkUcs2(body, Ucs2ConcatBytes);
        var reference = Random.Shared.Next(1, 256);
        var pdus = new List<string>(chunks.Count);

        for (var i = 0; i < chunks.Count; i++)
        {
            var udh = BuildConcatUdh(reference, chunks.Count, i + 1);
            var ud = new byte[udh.Length + chunks[i].Length];
            udh.CopyTo(ud, 0);
            chunks[i].CopyTo(ud, udh.Length);
            pdus.Add(BuildPdu(digits, international, "08", ud, ud.Length, hasUdh: true));
        }

        return pdus;
    }

    /// <summary>按 UCS2 字节上限切分，且不拆开代理对。</summary>
    private static List<byte[]> ChunkUcs2(string text, int maxBytes)
    {
        var chunks = new List<byte[]>();
        var current = new List<byte>(maxBytes);

        for (var i = 0; i < text.Length; i++)
        {
            var length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length &&
                         char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;

            if (current.Count + length * 2 > maxBytes && current.Count > 0)
            {
                chunks.Add([.. current]);
                current.Clear();
            }

            current.AddRange(Encoding.BigEndianUnicode.GetBytes(text.Substring(i, length)));
            i += length - 1;
        }

        if (current.Count > 0)
        {
            chunks.Add([.. current]);
        }

        return chunks;
    }

    /// <summary>
    /// 按 septet 上限切分，且不把「ESC + 扩展码」拆到两段。
    /// </summary>
    private static List<List<byte>> SplitSeptets(List<byte> septets, int maxSeptets)
    {
        var parts = new List<List<byte>>();
        var start = 0;

        while (start < septets.Count)
        {
            var length = Math.Min(maxSeptets, septets.Count - start);
            // 不能以 ESC 结尾：扩展字符必须与其码点同段。
            if (start + length < septets.Count && septets[start + length - 1] == 0x1B)
            {
                length--;
            }

            if (length <= 0)
            {
                length = Math.Min(maxSeptets, septets.Count - start);
            }

            parts.Add(septets.GetRange(start, length));
            start += length;
        }

        return parts;
    }

    private static byte[] BuildConcatUdh(int reference, int total, int sequence) =>
    [
        0x05, 0x00, 0x03, (byte)(reference & 0xFF), (byte)total, (byte)sequence,
    ];

    /// <summary>
    /// 组装 SMS-SUBMIT PDU。
    /// </summary>
    /// <param name="digits">纯号码数字（不含 +）。</param>
    /// <param name="international">是否国际格式（决定 TOA）。</param>
    /// <param name="dcs">TP-DCS。</param>
    /// <param name="userData">已打包的 TP-UD。</param>
    /// <param name="udl">TP-UDL：GSM7 为 septet 数（含 UDH），UCS2 为字节数（含 UDH）。</param>
    /// <param name="hasUdh">是否携带 UDH（置 TP-UDHI）。</param>
    private static string BuildPdu(
        string digits, bool international, string dcs, byte[] userData, int udl, bool hasUdh)
    {
        if (udl is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(udl), udl,
                "TP-UDL 超出单字节范围，正文未正确分段。");
        }

        // TP-MTI=01（SUBMIT），TP-VPF=10（相对有效期 1 字节）；带 UDH 时置 TP-UDHI(0x40)。
        var firstOctet = hasUdh ? "51" : "11";

        return "00"                                  // SMSC 信息长度 = 0
             + firstOctet
             + "00"                                  // TP-MR（消息参考号）
             + $"{digits.Length:X2}"                 // TP-DA 地址位数
             + (international ? "91" : "81")         // TP-DA TOA
             + EncodeNumberBcd(digits, international)
             + "00"                                  // TP-PID
             + dcs                                   // TP-DCS
             + "8F"                                  // TP-VP：12 小时，避免误用 00 导致 5 分钟后过期
             + $"{udl:X2}"                           // TP-UDL
             + Convert.ToHexString(userData);        // TP-UD
    }

    private static byte ReadSeptet(ReadOnlySpan<byte> data, long bitPos)
    {
        var value = 0;
        for (var i = 0; i < 7; i++)
        {
            var p = bitPos + i;
            var b = data[(int)(p >> 3)];
            value |= ((b >> (int)(p & 7)) & 1) << i;
        }
        return (byte)value;
    }

    private static bool CanEncodeGsm7(char c) =>
        Gsm7Lookup.ContainsKey(c) || Gsm7ExtensionLookup.ContainsKey(c);

    private static readonly Dictionary<char, byte> Gsm7Lookup = BuildGsm7Table();

    /// <summary>GSM 03.38 扩展表：以 ESC(0x1B) 为首字节的双 septet 字符。</summary>
    private static readonly Dictionary<char, byte> Gsm7ExtensionLookup = new()
    {
        ['\f'] = 0x0A,
        ['^'] = 0x14,
        ['{'] = 0x28,
        ['}'] = 0x29,
        ['\\'] = 0x2F,
        ['['] = 0x3C,
        ['~'] = 0x3D,
        [']'] = 0x3E,
        ['|'] = 0x40,
        ['€'] = 0x65,
    };

    private static readonly Dictionary<byte, char> ReverseGsm7Lookup =
        Gsm7Lookup.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

    private static readonly Dictionary<byte, char> Gsm7ExtensionReverseLookup =
        Gsm7ExtensionLookup.ToDictionary(kv => kv.Value, kv => kv.Key);

    private static Dictionary<char, byte> BuildGsm7Table()
    {
        // 0x1B 是 ESC（扩展表前缀），不参与基本表映射。
        const string basic =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ\u001BÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
            "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
        var table = new Dictionary<char, byte>(basic.Length);
        for (var i = 0; i < basic.Length; i++)
        {
            if (basic[i] == '\u001B')
            {
                continue;
            }
            table[basic[i]] = (byte)i;
        }
        return table;
    }
}
