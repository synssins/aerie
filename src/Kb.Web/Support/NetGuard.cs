using Kb.Core.Audit;
using Kb.Core.Content;
using Kb.Core.Spaces;
using Kb.Data;
using Kb.Plugins;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kb.Web;

/// <summary>Fetching a URL a user supplied: only public internet addresses, so the server can't be aimed at the internal network.</summary>
public static class NetGuard
{
    public const string ClientName = "remote-images";

    public static bool IsPublic(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)   // carrier-grade NAT
                || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var first = ip.GetAddressBytes()[0];
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6None) || (first & 0xFE) == 0xFC);
    }

    /// <summary>Checks the address actually connected to, not just the name, so a DNS answer can't switch after the check.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = false, // shortcut: redirects are not followed; follow them with a re-check per hop if sites need it
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var ip = addresses.FirstOrDefault(IsPublic) ?? throw new HttpRequestException("Address not allowed.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };
}
