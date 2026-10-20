using System.Net;
using CellPort.Core.Services;
using Xunit;

namespace CellPort.Core.Tests;

/// <summary>eUICC 通知地址（来自卡片的外部数据）的 SSRF 防护测试。</summary>
public sealed class NotificationEndpointPolicyTests
{
    [Theory]
    [InlineData("smdp.example.com")]
    [InlineData("rsp.truphone.com")]
    [InlineData("euicc.example.co.uk")]
    public void AllowsPublicDnsNames(string host) =>
        Assert.True(NotificationEndpointPolicy.IsAllowedHostName(host));

    [Theory]
    [InlineData("localhost")]
    [InlineData("device.local")]
    [InlineData("printer.localdomain")]
    [InlineData("svc.internal")]
    [InlineData("nas.lan")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("bad_host.example.com")]
    [InlineData("-leading.example.com")]
    public void RejectsLoopbackNamesAndIpLiterals(string host) =>
        Assert.False(NotificationEndpointPolicy.IsAllowedHostName(host));

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    public void ClassifiesIpv4Addresses(string address, bool expected) =>
        Assert.Equal(expected, NotificationEndpointPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("2606:4700:4700::1111", true)]
    public void ClassifiesIpv6Addresses(string address, bool expected) =>
        Assert.Equal(expected, NotificationEndpointPolicy.IsPublicAddress(IPAddress.Parse(address)));

    [Fact]
    public async Task RejectsHttpScheme()
    {
        var error = await NotificationEndpointPolicy.ValidateAsync(
            new Uri("http://smdp.example.com/gsma/rsp2/es9plus/handleNotification"),
            CancellationToken.None);

        Assert.NotNull(error);
    }
}
