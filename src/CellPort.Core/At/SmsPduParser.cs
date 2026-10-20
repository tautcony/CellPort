using System.Text;

namespace CellPort.Core.At;

/// <summary>解码后的一条短信。</summary>
public sealed class SmsMessage
{
    /// <summary>模块存储索引，用于删除。</summary>
    public int Index { get; set; } = -1;

    internal string? PduFingerprint { get; set; }

    /// <summary>对端号码。</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>正文。</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>服务中心时间戳。</summary>
    public DateTime? Timestamp { get; set; }

    /// <summary>方向。</summary>
    public SmsDirection Direction { get; set; } = SmsDirection.Incoming;

    /// <summary>状态。</summary>
    public SmsStatus Status { get; set; } = SmsStatus.Unread;

    /// <summary>长短信参考号；多条同参考号的短信属于同一会话。</summary>
    public int? ConcatReference { get; set; }

    /// <summary>长短信总段数。</summary>
    public int? ConcatTotal { get; set; }

    /// <summary>长短信当前段号（从 1 开始）。</summary>
    public int? ConcatPart { get; set; }

    /// <summary>是否为长短信的一段。</summary>
    public bool IsConcatPart => ConcatTotal is > 1 && ConcatPart is >= 1;

    /// <summary>提取到的验证码（若有）。</summary>
    public string? VerificationCode { get; set; }

    public override string ToString() =>
        $"[{(Direction == SmsDirection.Incoming ? "IN" : "OUT")}] {Number}: {Truncate(Body, 40)}";

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}

/// <summary>短信方向。</summary>
public enum SmsDirection
{
    /// <summary>收到。</summary>
    Incoming,
    /// <summary>发出。</summary>
    Outgoing,
}

/// <summary>短信状态。</summary>
public enum SmsStatus
{
    /// <summary>未读。</summary>
    Unread,
    /// <summary>已读。</summary>
    Read,
    /// <summary>未发送。</summary>
    Unsent,
    /// <summary>已发送。</summary>
    Sent,
}

/// <summary>
/// 短信 PDU 解析器。支持 DELIVER（接收）报文的完整解码，含 UCS2、GSM 7-bit 与长短信 UDH。
/// </summary>
public static class SmsPduParser
{
    /// <summary>
    /// 解析一条 +CMGL / +CMGR 返回的 PDU 字符串。
    /// </summary>
    /// <param name="pdu">十六进制 PDU（不含首尾引号）。</param>
    /// <returns>解析出的短信；失败返回 null。</returns>
    public static SmsMessage? Parse(string pdu)
    {
        try
        {
            var bytes = Convert.FromHexString(pdu.Trim().Trim('"'));
            var reader = new PduReader(bytes);

            // 第一字节：SMSC 地址长度（以字节计，已包含 type-of-address 字节）
            var smscLen = reader.ReadByte();
            reader.Skip(smscLen);

            var firstOctet = reader.ReadByte();
            var hasUdh = (firstOctet & 0x40) != 0;         // TP-UDHI
            var messageType = firstOctet & 0x03;           // TP-MTI
            var isSubmit = messageType == 0x01;

            // DELIVER、SUBMIT 都包含对端地址；SUBMIT 前面另有一个 TP-MR 字节。
            if (messageType is 0x02 or 0x03)
            {
                return null; // STATUS-REPORT 与保留类型暂不展开
            }
            if (isSubmit)
            {
                reader.Skip(1); // TP-MR
            }

            var number = ReadAddress(ref reader, out _);
            reader.ReadByte(); // TP-PID
            var dcs = reader.ReadByte();

            DateTime? timestamp;
            if (isSubmit)
            {
                // TP-VPF (bits 4..3): 00 none, 10 relative (1 octet),
                // 01 enhanced, 11 absolute (both 7 octets).
                var validityPeriodFormat = (firstOctet >> 3) & 0x03;
                reader.Skip(validityPeriodFormat switch { 0 => 0, 2 => 1, _ => 7 });
                timestamp = null;
            }
            else
            {
                timestamp = ReadTimestamp(ref reader); // TP-SCTS（7 字节 BCD）
            }

            var udLength = reader.ReadByte();
            var userData = reader.ReadRemaining();

            // UDH 处理：UDHL 字节 + UDH 本身，整体按字节计数（GSM7 解码需要该偏移）。
            int? concatRef = null, concatTotal = null, concatPart = null;
            var udhOctets = 0;
            if (hasUdh && userData.Length > 0)
            {
                var udhLen = userData[0];
                var available = Math.Min(udhLen, userData.Length - 1);
                ParseUdh(userData.AsSpan(1, available), ref concatRef, ref concatTotal, ref concatPart);
                udhOctets = Math.Min(1 + udhLen, userData.Length);
            }

            var body = DecodeUserData(userData, dcs, udLength, hasUdh, udhOctets);

            return new SmsMessage
            {
                Number = number,
                Body = body,
                PduFingerprint = Convert.ToHexString(bytes),
                Timestamp = timestamp,
                Direction = isSubmit ? SmsDirection.Outgoing : SmsDirection.Incoming,
                Status = isSubmit ? SmsStatus.Sent : SmsStatus.Unread,
                ConcatReference = concatRef,
                ConcatTotal = concatTotal,
                ConcatPart = concatPart,
                VerificationCode = ExtractVerificationCode(body),
            };
        }
        catch
        {
            return null;
        }
    }

    private static void ParseUdh(
        ReadOnlySpan<byte> udh,
        ref int? concatRef,
        ref int? concatTotal,
        ref int? concatPart)
    {
        var i = 0;
        while (i + 1 < udh.Length)
        {
            var iei = udh[i];
            var ieiLen = udh[i + 1];
            var dataStart = i + 2;
            if (dataStart + ieiLen > udh.Length)
            {
                break;
            }

            // IEI 0x00：8-bit 参考号；IEI 0x08：16-bit 参考号
            if (iei == 0x00 && ieiLen == 3)
            {
                concatRef = udh[dataStart];
                concatTotal = udh[dataStart + 1];
                concatPart = udh[dataStart + 2];
            }
            else if (iei == 0x08 && ieiLen == 4)
            {
                concatRef = (udh[dataStart] << 8) | udh[dataStart + 1];
                concatTotal = udh[dataStart + 2];
                concatPart = udh[dataStart + 3];
            }

            i = dataStart + ieiLen;
        }
    }

    private static string DecodeUserData(byte[] ud, byte dcs, int udLength, bool hasUdh, int udhOctets)
    {
        var encoding = (dcs >> 2) & 0x03; // 00=7bit, 01=8bit, 10=UCS2

        if (encoding == 0x02)
        {
            // UCS2：UDL 是字节数，且包含 UDH 占用的字节。
            var count = Math.Clamp(Math.Min(ud.Length, udLength) - udhOctets, 0, int.MaxValue);
            if (count % 2 != 0)
            {
                count--;
            }
            return count <= 0
                ? string.Empty
                : Encoding.BigEndianUnicode.GetString(ud, udhOctets, count);
        }

        if (encoding == 0x01)
        {
            var count = Math.Clamp(Math.Min(ud.Length, udLength) - udhOctets, 0, int.MaxValue);
            return count <= 0 ? string.Empty : Encoding.Latin1.GetString(ud, udhOctets, count);
        }

        // GSM 7-bit：无 UDH 时 UDL 就是正文 septet 数。
        if (!hasUdh)
        {
            return SmsCodec.DecodeGsm7Bit(ud, udLength);
        }

        // 有 UDH 时 UDL 是「UDH + 正文」的总 septet 数，且 6 字节 UDH 会补 1 个填充位，
        // 因此正文从第 ceil(udhOctets*8/7) 个 septet 开始。
        var udhSeptets = (udhOctets * 8 + 6) / 7;
        var septetCount = udLength - udhSeptets;
        return septetCount <= 0
            ? string.Empty
            : SmsCodec.DecodeGsm7Bit(ud, septetCount, startSeptet: udhSeptets);
    }

    private static string ReadAddress(ref PduReader reader, out byte typeOfAddress)
    {
        var addrLen = reader.ReadByte();
        typeOfAddress = reader.ReadByte();
        // TP-OA/TP-DA length counts address digits; TOA is a separate octet.
        if (addrLen <= 0)
        {
            return string.Empty;
        }

        var byteCount = (addrLen + 1) / 2;
        var raw = reader.ReadBytes(byteCount);

        var sb = new StringBuilder(addrLen);
        foreach (var b in raw)
        {
            sb.Append(NibbleToChar(b & 0x0F));
            sb.Append(NibbleToChar((b >> 4) & 0x0F));
        }

        var digits = sb.ToString().TrimEnd('F', 'f')[..Math.Min(addrLen, sb.Length)];

        // 0x91 = 国际格式
        var isInternational = (typeOfAddress & 0x70) == 0x10;
        return isInternational ? "+" + digits : digits;
    }

    private static char NibbleToChar(int n) =>
        n switch
        {
            >= 0 and <= 9 => (char)('0' + n),
            0x0A => '*',
            0x0B => '#',
            0x0C => 'a',
            0x0D => 'b',
            0x0E => 'c',
            _ => 'F',
        };

    private static DateTime? ReadTimestamp(ref PduReader reader)
    {
        try
        {
            var raw = reader.ReadBytes(7);

            var year = Dec(raw[0]);
            var month = Dec(raw[1]);
            var day = Dec(raw[2]);
            var hour = Dec(raw[3]);
            var minute = Dec(raw[4]);
            var second = Dec(raw[5]);

            // 时区：15 分钟为单位。符号位是「十位半字节」的最高位（0x08）——
            // 十位只可能取 0..7，该位因此空闲；个位半字节 0..9 全用，不能放符号。
            // 必须在 Dec() 之前按原始字节取值，否则符号位会被当成数字位。
            var tzOctet = raw[6];
            var negative = (tzOctet & 0x08) != 0;
            var tzTens = tzOctet & 0x07;
            var tzUnits = (tzOctet >> 4) & 0x0F;
            var tzQuarters = tzTens * 10 + tzUnits;
            var offsetMinutes = tzQuarters * 15 * (negative ? -1 : 1);

            var fullYear = year < 70 ? 2000 + year : 1900 + year;
            if (month is < 1 or > 12 || day is < 1 or > 31)
            {
                return null;
            }

            var local = new DateTime(fullYear, month, day, hour, minute,
                Math.Min(second, 59), DateTimeKind.Unspecified);
            var utc = local.AddMinutes(-offsetMinutes);
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        }
        catch
        {
            return null;
        }

        int Dec(int b) => ((b & 0x0F) * 10) + ((b >> 4) & 0x0F);
    }

    /// <summary>
    /// 从短信正文中提取验证码。
    /// </summary>
    public static string? ExtractVerificationCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        // 优先匹配「验证码/校验码/动态码/code」附近的 4-8 位数字
        var keywordPattern =
            @"(?:验证码|校验码|动态码|确认码|短信码|verification\s*code|verify\s*code|code)\D{0,12}(\d{4,8})";
        var m = System.Text.RegularExpressions.Regex.Match(
            body, keywordPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            return m.Groups[1].Value;
        }

        // 退化：独立出现的 4-6 位数字，且正文较短
        if (body.Length <= 120)
        {
            var m2 = System.Text.RegularExpressions.Regex.Match(body, @"(?<!\d)(\d{4,6})(?!\d)");
            if (m2.Success)
            {
                return m2.Groups[1].Value;
            }
        }

        return null;
    }
}

/// <summary>PDU 字节流读取器。</summary>
internal ref struct PduReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos = 0;

    public byte ReadByte()
    {
        if (_pos >= _data.Length)
        {
            throw new InvalidOperationException("PDU 数据不足。");
        }
        return _data[_pos++];
    }

    public byte[] ReadBytes(int count)
    {
        var n = Math.Min(count, _data.Length - _pos);
        var result = _data.Slice(_pos, n).ToArray();
        _pos += n;
        return result;
    }

    public byte[] ReadRemaining()
    {
        var result = _data[_pos..].ToArray();
        _pos = _data.Length;
        return result;
    }

    public void Skip(int count) => _pos = Math.Min(_pos + count, _data.Length);
}
