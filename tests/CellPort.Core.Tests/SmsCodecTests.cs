using Xunit;
using CellPort.Core.At;

namespace CellPort.Core.Tests;

/// <summary>GSM 7-bit / UCS2 编解码、BCD 号码编码与 SMS-SUBMIT PDU 构造的规范向量测试。</summary>
public class SmsCodecTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("hello hello")]
    [InlineData("The quick brown fox jumps over the lazy dog 0123456789")]
    [InlineData("OTP 123456 is your code.")]
    public void Gsm7Bit_RoundTrip(string text)
    {
        var septets = SmsCodec.ToSeptets(text);
        var encoded = SmsCodec.PackSeptets([], septets);
        Assert.Equal(text, SmsCodec.DecodeGsm7Bit(encoded, septets.Count));
    }

    [Fact]
    public void Gsm7Bit_HelloHello_StandardVector()
    {
        // 3GPP TS 23.038 官方示例："hellohello"（10 septet）→ 9 字节
        Assert.Equal(
            "E8329BFD4697D9EC37",
            Convert.ToHexString(SmsCodec.EncodeGsm7Bit("hellohello")));
    }

    [Fact]
    public void Gsm7Bit_Ab12_StandardVector()
    {
        // 手算：A=0x41 B=0x42 1=0x31 2=0x32 → 打包 41 61 4C 06
        Assert.Equal(
            new byte[] { 0x41, 0x61, 0x4C, 0x06 },
            SmsCodec.EncodeGsm7Bit("AB12"));
    }

    [Fact]
    public void Gsm7Bit_ExtensionCharacters_UseEscapeSeptet()
    {
        // '[' '~' ']' 属于扩展表：ESC(0x1B) + 码点，每字符占 2 septet。
        var septets = SmsCodec.ToSeptets("[~]");
        Assert.Equal(new byte[] { 0x1B, 0x3C, 0x1B, 0x3D, 0x1B, 0x3E }, septets);
        Assert.Equal("[~]", SmsCodec.DecodeGsm7Bit(SmsCodec.PackSeptets([], septets), septets.Count));
    }

    [Fact]
    public void Gsm7Bit_UnsupportedCharacterThrows()
    {
        Assert.Throws<ArgumentException>(() => SmsCodec.EncodeGsm7Bit("箭头 →"));
    }

    [Fact]
    public void RequiresUcs2_DetectsUnencodableCharacters()
    {
        Assert.False(SmsCodec.RequiresUcs2("Hello 123"));
        Assert.True(SmsCodec.RequiresUcs2("你好"));
        // é 在 GSM 基本表（0x05）内，可用 7-bit 发送。
        Assert.False(SmsCodec.RequiresUcs2("café"));
        // 扩展表字符同样可编码，不应退化为 UCS2。
        Assert.False(SmsCodec.RequiresUcs2("[x] ~ | €"));
        Assert.True(SmsCodec.RequiresUcs2("→"));
    }

    [Fact]
    public void EncodeForText_Chinese_UsesUcs2()
    {
        var (hex, dcs) = SmsCodec.EncodeForText("你好");
        Assert.Equal("08", dcs);
        Assert.Equal("4F60597D", hex);
        Assert.Equal("你好", SmsCodec.DecodeUcs2(hex));
    }

    [Fact]
    public void EncodeForText_Ascii_UsesGsm7()
    {
        var (hex, dcs) = SmsCodec.EncodeForText("AB12");
        Assert.Equal("00", dcs);
        Assert.Equal("41614C06", hex);
    }

    [Theory]
    [InlineData("13800138000", false, "3108108300F0")]
    // 3GPP TS 23.040 §9.1.2.5 的国际号码示例：+33123456789 → 0B 91 33 21 43 65 87 F9。
    [InlineData("+33123456789", true, "3321436587F9")]
    [InlineData("+8613800138000", true, "683108108300F0")]
    public void EncodeNumberBcd_SwapsNibblesWithoutPrefix(string number, bool international, string expected)
    {
        // 号码本身不含任何前缀：「国际格式」只由独立的 TOA 字节（0x91）表达。
        Assert.Equal(expected, SmsCodec.EncodeNumberBcd(number, international));
    }

    [Theory]
    [InlineData("+8613800138000", "Test")]
    [InlineData("13800138000", "hellohello")]
    [InlineData("+33123456789", "验证码 123456，请勿泄露。")]
    [InlineData("+8613800138000", "code [42] ~ ok | €")]
    public void BuildSubmitPdus_SingleSegment_RoundTripsThroughParser(string number, string body)
    {
        var pdus = SmsCodec.BuildSubmitPdus(number, body);

        var pdu = Assert.Single(pdus);
        // SMSC 长度 0 + SUBMIT(0x11，无 UDH) + TP-MR + 地址长度 + TOA(0x91/0x81)
        Assert.StartsWith("001100", pdu);
        Assert.Equal(number.StartsWith('+') ? "91" : "81", pdu[8..10]);

        var parsed = SmsPduParser.Parse(pdu);
        Assert.NotNull(parsed);
        Assert.Equal(number, parsed.Number);
        Assert.Equal(body, parsed.Body);
        Assert.Equal(SmsDirection.Outgoing, parsed.Direction);
        Assert.Null(parsed.ConcatPart);
    }

    [Fact]
    public void BuildSubmitPdus_LongAscii_SplitsIntoSingleSeptetBoundedSegments()
    {
        var body = new string('A', 200);
        var pdus = SmsCodec.BuildSubmitPdus("+8613800138000", body);

        Assert.Equal(2, pdus.Count);

        var bytes = pdus.Select(Convert.FromHexString).ToArray();
        // 第一段 153 septet、第二段 47 septet；TP-UDL = UDH(7) + 正文 septet。
        Assert.Equal(SmsCodec.ConcatUdhSeptets + 153, bytes[0][UdlOffset(bytes[0])]);
        Assert.Equal(SmsCodec.ConcatUdhSeptets + 47, bytes[1][UdlOffset(bytes[1])]);
        Assert.All(bytes, b => Assert.True(b.Length - 1 <= 160, $"TPDU 超长：{b.Length - 1}"));
        // 带 UDH 时首字节的 TP-UDHI(0x40) 必须置位。
        Assert.All(bytes, b => Assert.Equal(0x51, b[1]));

        var parts = pdus.Select(SmsPduParser.Parse).ToArray();
        Assert.All(parts, p => Assert.Equal(2, p!.ConcatTotal));
        Assert.Equal([1, 2], parts.Select(p => p!.ConcatPart).ToArray());
        Assert.Equal(body, string.Concat(parts.Select(p => p!.Body)));
    }

    [Fact]
    public void BuildSubmitPdus_ExtensionCharactersAreNotSplitAcrossSegments()
    {
        // 100 个 '['（每字符 2 septet）→ 200 septet，必须分成两段，
        // 且不能把「ESC + 码点」拆到两段（否则解析出乱码或丢字符）。
        var body = new string('[', 100);
        var pdus = SmsCodec.BuildSubmitPdus("13800138000", body);

        var parts = pdus.Select(SmsPduParser.Parse).ToArray();
        Assert.All(parts, p => Assert.Equal(2, p!.ConcatTotal));
        Assert.Equal(body, string.Concat(parts.Select(p => p!.Body)));
    }

    [Fact]
    public void BuildSubmitPdus_LongChinese_SplitsAtUcs2Limit()
    {
        var body = new string('中', 80);
        var pdus = SmsCodec.BuildSubmitPdus("+8613800138000", body);

        Assert.Equal(2, pdus.Count);

        var parts = pdus.Select(SmsPduParser.Parse).ToArray();
        Assert.All(parts, p => Assert.Equal(2, p!.ConcatTotal));
        Assert.Equal(body, string.Concat(parts.Select(p => p!.Body)));
        Assert.Equal(67, parts[0]!.Body.Length);
        Assert.Equal(13, parts[1]!.Body.Length);
        // 第一段 TP-UDL = UDH(6 字节) + 134 字节正文。
        var bytes = Convert.FromHexString(pdus[0]);
        Assert.Equal(140, bytes[UdlOffset(bytes)]);
    }

    [Fact]
    public void BuildSubmitPdus_Ucs2AtSingleSegmentLimitStaysSingle()
    {
        // 70 个 BMP 字符 = 140 字节，正好是单条上限。
        var body = new string('字', 70);
        var pdu = Assert.Single(SmsCodec.BuildSubmitPdus("+8613800138000", body));

        var bytes = Convert.FromHexString(pdu);
        Assert.Equal(156, bytes.Length);
        Assert.Equal(140, bytes[UdlOffset(bytes)]);

        var parsed = SmsPduParser.Parse(pdu);
        Assert.Equal(body, parsed!.Body);
        Assert.Null(parsed.ConcatPart);
    }

    [Fact]
    public void BuildSubmitPdus_RejectsEmptyNumber()
    {
        Assert.Throws<ArgumentException>(() => SmsCodec.BuildSubmitPdus("+", "hello"));
    }

    /// <summary>TP-UDL 在 PDU 中的字节偏移：SMSC(1)+FO(1)+MR(1)+addrLen(1)+TOA(1)+addr+PID+DCS+VP。</summary>
    private static int UdlOffset(byte[] pdu)
    {
        var addressOctets = (pdu[3] + 1) / 2;
        return 5 + addressOctets + 3;
    }
}
