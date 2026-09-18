using System.Net.Http;

namespace VlessTunnel.Core;

/// <summary>
/// Проверка системных часов для <c>doctor</c> (план, 3.9: "REALITY и TLS
/// ломаются при сбитых часах (часто в виртуалках) — doctor сверяет время
/// (NTP или заголовок Date через туннель) и предупреждает"). Через
/// HTTP-заголовок <c>Date</c>, не отдельный NTP-клиент — не тянем ещё один
/// протокол/порт ради одной проверки, когда HTTPS:443 и так уже нужен
/// остальному приложению и почти никогда не блокируется файрволами.
/// </summary>
public static class ClockCheck
{
    public sealed record Result(bool Ok, double? SkewSeconds, string? Error);

    public static async Task<Result> CheckAsync(HttpClient? client = null, CancellationToken ct = default)
    {
        var http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, "https://www.cloudflare.com/");
            var before = DateTimeOffset.UtcNow;
            using var resp = await http.SendAsync(req, ct);
            var after = DateTimeOffset.UtcNow;
            if (resp.Headers.Date is not { } serverDate)
                return new Result(false, null, "сервер не вернул заголовок Date");

            // Половина RTT как грубая компенсация сетевой задержки — не
            // претендуем на точность NTP, только на то, чтобы не пропустить
            // расхождение в минуты/часы/дни (именно оно ломает REALITY/TLS).
            var localNow = before + (after - before) / 2;
            return new Result(true, (localNow - serverDate).TotalSeconds, null);
        }
        catch (Exception ex)
        {
            return new Result(false, null, ex.Message);
        }
        finally
        {
            if (client is null) http.Dispose();
        }
    }
}
