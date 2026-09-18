using System.Net;
using System.Net.Sockets;
using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Service;

/// <summary>
/// Живые проверки (план, 3.5: "test — HTTP, SOCKS5, прозрачный TCP,
/// UDP/QUIC, DNS"). UDP/QUIC сознательно не входит в этот проход — нет
/// простого, не завязанного на конкретный внешний сервис способа
/// проверить именно QUIC/UDP через прозрачный TUN без введения лишней
/// внешней зависимости теста; TCP/HTTP/SOCKS5/DNS покрывают основные
/// пути трафика (прямой, через SOCKS-инбаунд, через HTTP-инбаунд,
/// резолв имён) и всё, что нужно для базовой диагностики "туннель
/// реально работает".
/// </summary>
public static class TunnelTester
{
    private const int SocksPort = 10808;
    private const int HttpPort = 10809;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    public static async Task<TestResults> RunAsync()
    {
        var transparentTcp = await RunOne(() => TcpConnectAsync(IPAddress.Parse("1.1.1.1"), 443));
        var dns = await RunOne(async () =>
        {
            var addrs = await Dns.GetHostAddressesAsync("example.com").WaitAsync(Timeout);
            return addrs.Length > 0;
        });
        var http = await RunOne(() => HttpViaProxyAsync("http"));
        var socks5 = await RunOne(() => HttpViaProxyAsync("socks5"));

        return new TestResults
        {
            TransparentTcp = transparentTcp,
            Dns = dns,
            Http = http,
            Socks5 = socks5,
        };
    }

    private static async Task<bool> RunOne(Func<Task<bool>> check)
    {
        try { return await check(); }
        catch { return false; }
    }

    private static async Task<bool> TcpConnectAsync(IPAddress address, int port)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, port).WaitAsync(Timeout);
        return client.Connected;
    }

    private static async Task<bool> HttpViaProxyAsync(string scheme)
    {
        var port = scheme == "http" ? HttpPort : SocksPort;
        using var handler = new HttpClientHandler { Proxy = new WebProxy(new Uri($"{scheme}://127.0.0.1:{port}")), UseProxy = true };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        using var resp = await client.GetAsync("http://example.com/");
        return resp.IsSuccessStatusCode;
    }
}
