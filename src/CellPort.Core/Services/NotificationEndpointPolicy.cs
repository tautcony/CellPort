using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CellPort.Core.Services;

/// <summary>
/// eUICC 通知上报（ES9+ / handleNotification）的服务端地址校验。
///
/// 通知里的 Address 来自 eUICC 卡片本身，属于「外部数据」：恶意或异常的卡片
/// 可以把请求指向任意主机，形成 SSRF（例如探测内网、打云元数据接口）。
/// 这里只允许「https + 公网 DNS 域名」，并配合禁用重定向把风险面收窄。
/// </summary>
internal static class NotificationEndpointPolicy
{
    /// <summary>
    /// 校验目标地址是否允许发起通知请求。返回 null 表示通过，否则为错误说明。
    /// </summary>
    public static async Task<string?> ValidateAsync(Uri uri, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "删除通知地址必须使用 HTTPS";
        }

        if (uri.HostNameType is not UriHostNameType.Dns || !IsAllowedHostName(uri.Host))
        {
            return "删除通知地址不在允许范围内";
        }

        try
        {
            _ = await ResolvePublicAddressesAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch
        {
            return "删除通知地址无法解析";
        }

        return null;
    }

    /// <summary>
    /// 解析并校验连接目标。调用方必须直接连接返回的 IP，不能再按主机名做第二次
    /// DNS 查询，否则攻击者可在校验与连接之间切换解析结果（DNS rebinding）。
    /// </summary>
    public static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken ct)
    {
        if (!IsAllowedHostName(host))
        {
            throw new InvalidOperationException("删除通知地址不在允许范围内");
        }

        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new InvalidOperationException("删除通知地址无法解析");
        }

        if (addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new InvalidOperationException("删除通知地址指向非公网地址");
        }

        return addresses;
    }

    /// <summary>按已校验的 DNS 结果建立 TCP 连接，TLS 仍由 HttpClient 按原 URI 主机名验证。</summary>
    public static async ValueTask<Stream> ConnectPublicEndpointAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException("无法连接到已验证的通知服务器地址。", lastError);
    }

    /// <summary>
    /// 主机名层面的粗筛：拒绝回环/内网专用后缀与非法字符。
    /// </summary>
    public static bool IsAllowedHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return false;
        }

        var name = host.TrimEnd('.');
        if (name.Length == 0 || !name.Contains('.'))
        {
            // SM-DP+ 一定是完整域名；单标签主机名（localhost 等）直接拒绝。
            return false;
        }

        // IP 字面量（如 127.0.0.1）不应走域名分支。
        if (IPAddress.TryParse(name, out _))
        {
            return false;
        }

        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63)
            {
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    return false;
                }
            }
        }

        string[] blockedSuffixes = [".local", ".localhost", ".internal", ".localdomain", ".home", ".corp", ".lan"];
        foreach (var suffix in blockedSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 地址层面的细筛：回环、私网、链路本地、CGNAT、组播与保留段一律拒绝。
    /// </summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            var c = bytes[2];
            return !(
                a is 0 or 10 or 127 ||
                a == 100 && b is >= 64 and <= 127 ||       // CGNAT
                a == 169 && b == 254 ||                    // 链路本地
                a == 172 && b is >= 16 and <= 31 ||         // 私网
                a == 192 && b == 168 ||                    // RFC 1918 私网
                a == 192 && b == 0 && c == 0 ||             // 192.0.0.0/24 特殊用途
                a == 192 && b == 0 && c == 2 ||             // TEST-NET-1
                a == 192 && b == 88 && c == 99 ||           // 6to4 relay anycast
                a == 198 && b is 18 or 19 ||                // benchmark
                a == 198 && b == 51 && c == 100 ||          // documentation
                a == 203 && b == 0 && c == 113 ||           // documentation
                a >= 224);                                  // multicast/reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return false;
            }

            // 仅允许 2000::/3 global unicast，并排除特殊用途/文档网段。
            var globalUnicast = bytes[0] is >= 0x20 and <= 0x3F;
            var uniqueLocal = (bytes[0] & 0xFE) == 0xFC;
            var documentation = bytes[0] == 0x20 && bytes[1] == 0x01 &&
                                 bytes[2] == 0x0D && bytes[3] == 0xB8; // 2001:db8::/32
            var special2001 = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01 ||
                              bytes[0] == 0x20 && bytes[1] == 0x02; // 2001::/23 and 6to4
            var documentation3fff = bytes[0] == 0x3F && (bytes[1] & 0xF0) == 0xF0; // 3fff::/20
            return globalUnicast && !uniqueLocal && !documentation && !special2001 && !documentation3fff;
        }

        return false;
    }
}
