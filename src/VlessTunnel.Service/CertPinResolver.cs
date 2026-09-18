using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VlessTunnel.Service;

/// <summary>
/// Ревью п.12: план (3.1) требует "allowInsecure → получить отпечаток
/// сертификата и прописать pinnedPeerCertSha256, как cert-pin на Linux"
/// — этого не было реализовано НИГДЕ в решении, из-за чего
/// <c>OutboundBuilder</c> (CoreFamily=modern, дефолт) при
/// <c>allowInsecure=1</c> не ставил ни <c>allowInsecure</c> (это поле
/// только для legacy-ядра), ни <c>pinnedPeerCertSha256</c> (нечего было
/// подставить) — итог: ссылка, рассчитанная на самоподписанный
/// сертификат, молча подключается со СТРОГОЙ проверкой сертификата и не
/// работает, без единой подсказки почему.
///
/// Порт <c>cmd_cert_pin</c> из эталона (vless-tunnel.sh, ~src/vless-
/// tunnel, только для чтения): TLS-рукопожатие БЕЗ проверки сертификата
/// (в этом весь смысл — сервер и так самоподписанный), берём SHA256 от
/// сырых DER-байт самого сертификата И всей цепочки, которую successfully
/// удалось построить (дубликаты — один раз), возвращаем через запятую —
/// тот же формат, что Xray принимает в <c>pinnedPeerCertSha256</c>
/// (список через запятую — совпадение ЛЮБОГО из них считается валидным).
/// </summary>
public static class CertPinResolver
{
    public static async Task<string?> ResolveAsync(string host, int port, string sni, CancellationToken ct)
    {
        var pins = new List<string>();
        try
        {
            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(host, port, timeoutCts.Token);

            using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, chain, _) =>
                {
                    if (certificate is not null) AddPin(pins, certificate);
                    if (chain is not null)
                        foreach (var element in chain.ChainElements)
                            AddPin(pins, element.Certificate);
                    return true; // allowInsecure — задача не проверить, а собрать отпечатки
                });

            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni.Length > 0 ? sni : host,
            }, timeoutCts.Token);
        }
        catch
        {
            return null; // как и в эталоне — тихий отказ, вызывающий код сам решает, что логировать
        }
        return pins.Count > 0 ? string.Join(",", pins) : null;
    }

    private static void AddPin(List<string> pins, X509Certificate certificate)
    {
        var hash = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
        if (!pins.Contains(hash)) pins.Add(hash);
    }
}
