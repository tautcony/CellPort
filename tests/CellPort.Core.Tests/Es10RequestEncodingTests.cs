using Xunit;
using CellPort.Core.Services;

namespace CellPort.Core.Tests;

/// <summary>
/// ES10c 请求报文编码的逐字节验证（与 lpac es10c.c 对齐）。
/// 标准 ISD-P AID：A0000005591010FFFFFFFF8900000100（16 字节）。
/// </summary>
public class Es10RequestEncodingTests
{
    private const string Iccid = "8986011234567890123";

    [Fact]
    public void EnableProfile_UsesIccidAndRefreshTrue()
    {
        // BF31 <len> A0 <len> 5A 0A <ICCID BCD> 81 01 FF
        var req = LogicalChannelEs10.BuildProfileStateRequest(enable: true, Iccid);

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x31, req[1]);
        Assert.Equal(0x11, req[2]);   // A0 TLV 总长 = 17
        Assert.Equal(0xA0, req[3]);
        Assert.Equal(0x0F, req[4]);   // 5A TLV(12) + 81 TLV(3)
        Assert.Equal(0x5A, req[5]);   // ICCID identifier
        Assert.Equal(0x0A, req[6]);
        Assert.Equal(
            new byte[] { 0x98, 0x68, 0x10, 0x21, 0x43, 0x65, 0x87, 0x09, 0x21, 0xF3 },
            req[7..17]);
        Assert.Equal(new byte[] { 0x81, 0x01, 0xFF }, req[17..]); // refreshFlag = TRUE
        Assert.Equal(20, req.Length);
    }

    [Fact]
    public void DisableProfile_UsesBf32()
    {
        var req = LogicalChannelEs10.BuildProfileStateRequest(enable: false, Iccid);
        Assert.Equal(0x32, req[1]);
        // refresh=true 与 EnableProfile 一致
        var en = LogicalChannelEs10.BuildProfileStateRequest(enable: true, Iccid);
        for (var i = 0; i < req.Length; i++)
        {
            if (i == 1)
            {
                continue;
            }

            Assert.Equal(en[i], req[i]);
        }
    }

    [Fact]
    public void DeleteProfile_NoA0Wrapper_NoRefreshFlag()
    {
        // BF33 <len> 5A 0A <ICCID BCD> —— DeleteProfile 不带 refresh flag
        var req = LogicalChannelEs10.BuildDeleteProfileRequest(Iccid);

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x33, req[1]);
        Assert.Equal(0x0C, req[2]);   // 内容 = 5A TLV = 2 + 10
        Assert.Equal(0x5A, req[3]);
        Assert.Equal(0x0A, req[4]);
        Assert.Equal(10, req.Length - 5);
        Assert.Equal(15, req.Length);
    }

    [Fact]
    public void DeleteProfile_ByIccid_UsesTag5A()
    {
        // 非 32 字符 id 按 ICCID 处理：BF33 <len> 5A 0A <bcd10>
        var req = LogicalChannelEs10.BuildDeleteProfileRequest("89860112345678901234");

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x33, req[1]);
        Assert.Equal(12, req[2]);
        Assert.Equal(0x5A, req[3]);
        Assert.Equal(0x0A, req[4]);
        Assert.Equal(10, req.Length - 5);
    }

    [Fact]
    public void ListNotifications_UsesBf28AndDeleteEventFilter()
    {
        Assert.Equal(new byte[] { 0xBF, 0x28, 0x00 }, LogicalChannelEs10.BuildListNotificationsRequest());
        Assert.Equal(
            new byte[] { 0xBF, 0x28, 0x04, 0x81, 0x02, 0x04, 0x10 },
            LogicalChannelEs10.BuildListNotificationsRequest(eventId: 3));
    }

    [Fact]
    public void RetrieveNotification_UsesSequenceSearchCriteria()
    {
        Assert.Equal(
            new byte[] { 0xBF, 0x2B, 0x05, 0xA0, 0x03, 0x80, 0x01, 0x01 },
            LogicalChannelEs10.BuildRetrieveNotificationRequest(1));
    }

    [Fact]
    public void GetProfilesInfoRequestsDjOneHubBasicProfileTags()
    {
        Assert.Equal(
            new byte[] { 0xBF, 0x2D, 0x09, 0x5C, 0x07, 0x5A, 0x9F, 0x70, 0x90, 0x91, 0x92, 0x95 },
            LogicalChannelEs10.BuildProfileListRequest());
    }

    [Fact]
    public void ProfileTextDecodesUtf8WithoutLoss()
    {
        var encoded = "旅行卡"u8.ToArray();

        Assert.Equal("旅行卡", LogicalChannelEs10.DecodeProfileText(encoded));
    }

    [Fact]
    public void SetNickname_MatchesLpacEncoding()
    {
        // BF29 <len> 5A 0A <iccid bcd 10> 90 08 "CellPort"
        var req = LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", "CellPort");

        Assert.Equal(0xBF, req[0]);
        Assert.Equal(0x29, req[1]);
        Assert.Equal(14 + 8, req[2]);   // 5A TLV(12) + 90 TLV(2+8)
        Assert.Equal(0x5A, req[3]);
        Assert.Equal(0x0A, req[4]);
        Assert.Equal(25, req.Length);   // 5(头) + 10(ICCID BCD) + 2(90+len) + 8(昵称)
        // ICCID BCD：低 nibble 在前，19 位补 F
        Assert.Equal(
            new byte[] { 0x98, 0x68, 0x10, 0x21, 0x43, 0x65, 0x87, 0x09, 0x21, 0xF3 },
            req[5..15]);
        Assert.Equal(0x90, req[15]);  // ContextSpecific(16) nickname tag
        Assert.Equal(0x08, req[16]);  // nickname 长度
        Assert.Equal("CellPort", System.Text.Encoding.UTF8.GetString(req[17..]));
    }

    [Fact]
    public void SetNickname_EmptyNickname_EncodesLen0()
    {
        // 90 00 —— 长度 0 即清除昵称
        var req = LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", string.Empty);

        Assert.Equal(0x90, req[15]);
        Assert.Equal(0x00, req[16]);
        Assert.Equal(17, req.Length);
        Assert.Equal(14, req[2]);
    }

    [Fact]
    public void SetNickname_RejectsNamesLongerThan64Utf8Bytes()
    {
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", new string('a', 65)));
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", new string('中', 22)));
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("8986011234567890123", "\uD800"));
        Assert.Throws<ArgumentException>(
            () => LogicalChannelEs10.BuildNicknameRequest("12345", "ok")); // ICCID 太短
    }

    [Fact]
    public void IccidBcd_RoundTripWithIccidFromBcd()
    {
        const string iccid = "8986011234567890123";
        var bcd = LogicalChannelEs10.IccidToBcd(iccid);
        Assert.Equal(iccid, LogicalChannelEs10.IccidFromBcd(bcd));
    }

    [Fact]
    public void IccidBcd_TwentyDigits_NoPadding()
    {
        const string iccid = "89860112345678901234";
        var bcd = LogicalChannelEs10.IccidToBcd(iccid);
        Assert.Equal(10, bcd.Length);
        Assert.Equal(0x43, bcd[9]); // 末字节低 nibble 在前："3"低 + "4"高
        Assert.Equal(iccid, LogicalChannelEs10.IccidFromBcd(bcd));
    }
}
