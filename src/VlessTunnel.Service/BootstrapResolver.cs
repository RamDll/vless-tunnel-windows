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

        var addresses = await ResolveSetAsync(link, ct);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
    }

    /// <summary>
    /// Весь набор адресов из ответа резолвера, не только первый (ревью
    /// п.17) — периодический перерезолв (план, 3.2) сравнивает ТЕКУЩИЙ
    /// IP с этим набором целиком, а не с "первым адресом нового ответа":
    /// DNS round-robin меняет порядок записей от запроса к запросу, и
    /// сравнение только по первому адресу перезапускало бы туннель на
    /// ровном месте каждый цикл, даже когда набор адресов сервера не
    /// менялся вовсе.
    /// </summary>
    public static async Task<IReadOnlyList<IPAddress>> ResolveSetAsync(ParsedLink link, CancellationToken ct)
    {
        if (link.HostIsIp) return [IPAddress.Parse(link.Host)];

        var addresses = await Dns.GetHostAddressesAsync(link.Host, ct);
        if (addresses.Length == 0)
            throw new InvalidOperationException($"Bootstrap-резолв не дал ни одного адреса для {link.Host}");

        return addresses;
    }
}
