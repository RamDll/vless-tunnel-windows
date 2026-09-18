using System.Net;
using System.Net.Sockets;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Service;

/// <summary>
/// Bootstrap-резолв домена сервера системным резолвером ДО поднятия TUN
/// (план, 3.2, п.1) — отдельного DNS-исходящего Xray для этого не нужно,
/// IP просто подставляется в vnext.address при сборке config.json.
/// </summary>
public static class BootstrapResolver
{
    public static async Task<IPAddress> ResolveAsync(ParsedLink link, CancellationToken ct)
    {
        if (link.HostIsIp) return IPAddress.Parse(link.Host);

        var addresses = await Dns.GetHostAddressesAsync(link.Host, ct);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Bootstrap-резолв не дал ни одного адреса для {link.Host}");

        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }
}
