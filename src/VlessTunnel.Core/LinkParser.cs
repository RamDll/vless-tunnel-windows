using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Core;

/// <summary>
/// Разбор vless:// ссылки. Порт функции parse_link() из py_backend()
/// (vless-tunnel.sh, эталон v1.2.9) — построчное соответствие сохранено
/// намеренно ради golden-тестов паритета (план, 3.1/5.7). Любое расхождение
/// с эталоном здесь означает разное поведение клиента на Linux и Windows
/// для одной и той же ссылки — это то, чего план явно требует избежать.
/// </summary>
public static partial class LinkParser
{
    private static readonly string[] Networks =
        ["tcp", "raw", "ws", "grpc", "http", "h2", "quic", "kcp", "httpupgrade", "xhttp", "splithttp"];

    private static readonly string NetworksSortedForError =
        string.Join(", ", Networks.Distinct().OrderBy(s => s, StringComparer.Ordinal));

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-._~%!$&'()*+,;=:@/?\[\]#]+$")]
    private static partial Regex UrlSafeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]")]
    private static partial Regex StartsAlnumRegex();

    [GeneratedRegex(@"^vless://", RegexOptions.IgnoreCase)]
    private static partial Regex VlessSchemeRegex();

    [GeneratedRegex(@"vless://", RegexOptions.IgnoreCase)]
    private static partial Regex VlessSchemeAnywhereRegex();

    [GeneratedRegex(@"^vless://\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VlessEmptyRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSchemeRegex();

    [GeneratedRegex(@"[,\s]+")]
    private static partial Regex CommaOrWhitespaceRegex();

    [DoesNotReturn]
    private static void Die(string message) => throw new LinkParseException(message);

    private static bool IsIp(string s) => IPAddress.TryParse(s, out _);

    /// <summary>Percent-декодирование без замены '+' на пробел (как urllib.parse.unquote).</summary>
    private static string UnquotePlain(string s)
    {
        if (s.Length == 0) return s;
        try { return Uri.UnescapeDataString(s); }
        catch (UriFormatException) { return s; }
    }

    /// <summary>Как значения в query-строке: '+' -> пробел, потом percent-декодирование
    /// (см. urllib.parse.parse_qsl, которое перед unquote заменяет '+' на ' ').</summary>
    private static string UnquoteQueryValue(string s) => UnquotePlain(s.Replace('+', ' '));

    private static (string Host, string Port) SplitHostPort(string hp)
    {
        hp = hp.Trim();
        string host, port;
        if (hp.StartsWith('['))
        {
            var i = hp.IndexOf(']');
            if (i < 0) { Die("некорректный IPv6-адрес в ссылке"); }
            host = hp[1..i];
            var rest = hp[(i + 1)..];
            port = rest.StartsWith(':') ? rest[1..] : "";
        }
        else if (hp.Count(c => c == ':') >= 2)
        {
            host = hp;
            port = "";
        }
        else if (hp.Contains(':'))
        {
            var idx = hp.LastIndexOf(':');
            host = hp[..idx];
            port = hp[(idx + 1)..];
        }
        else
        {
            host = hp;
            port = "";
        }
        return (UnquotePlain(host).Trim(), port.Trim());
    }

    private static Dictionary<string, string> ParseQuery(string qs)
    {
        var result = new Dictionary<string, string>();
        if (qs.Length == 0) return result;
        foreach (var part in qs.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            string rawKey, rawVal;
            if (eq < 0) { rawKey = part; rawVal = ""; }
            else { rawKey = part[..eq]; rawVal = part[(eq + 1)..]; }
            var key = UnquoteQueryValue(rawKey).Trim().ToLowerInvariant();
            var val = UnquoteQueryValue(rawVal);
            result[key] = val;
        }
        return result;
    }

    private static string Get(IReadOnlyDictionary<string, string> q, string key) =>
        q.TryGetValue(key, out var v) ? v : "";

    /// <summary>Как (q.get(X) or fallback).strip() or fallback в эталоне: пустое
    /// или состоящее из пробелов значение (включая отсутствующий параметр)
    /// заменяется на fallback, а не остаётся пустой строкой.</summary>
    private static string TrimOrDefault(string raw, string fallback)
    {
        var trimmed = raw.Trim();
        return trimmed.Length > 0 ? trimmed : fallback;
    }

    public static ParsedLink Parse(string? link)
    {
        var raw = (link ?? "").Trim().Trim('"').Trim('\'');

        // Если вместе со ссылкой вставили лишний текст (например, строку
        // приглашения), берём последний «токен», который начинается с vless://,
        // и склеиваем к нему URL-подобные хвосты (ссылку могло разорвать
        // переносом строки).
        var toks = WhitespaceRunRegex().Split(raw);
        var candidates = new List<string>();
        for (var i = 0; i < toks.Length; i++)
        {
            var t = toks[i];
            if (!VlessSchemeRegex().IsMatch(t)) continue;
            var acc = new StringBuilder(t);
            for (var k = i + 1; k < Math.Min(i + 3, toks.Length); k++)
            {
                var tk = toks[k];
                if (tk.Length == 0 || !UrlSafeRegex().IsMatch(tk)) break;
                if (!StartsAlnumRegex().IsMatch(tk)) break; // продолжение ссылки, а не «==» / «---»
                if (VlessSchemeRegex().IsMatch(tk)) break;  // не приклеиваем вторую ссылку
                acc.Append(tk);
            }
            candidates.Add(acc.ToString());
        }
        link = candidates.Count > 0 ? candidates.OrderByDescending(c => c.Length).First() : raw;

        var m = VlessSchemeAnywhereRegex().Match(link);
        if (m.Success && m.Index > 0) link = link[m.Index..];
        link = WhitespaceRunRegex().Replace(link, "");

        if (link.Length == 0) { Die("пустая ссылка"); }
        if (!GenericSchemeRegex().IsMatch(link) && link.Contains('@'))
        {
            link = "vless://" + link;
        }
        if (!VlessSchemeRegex().IsMatch(link)) { Die("ожидается ссылка, начинающаяся с vless://"); }
        if (VlessEmptyRegex().IsMatch(link)) { Die("ссылка пустая"); }

        var body = link["vless://".Length..];
        var frag = "";
        var hashIdx = body.IndexOf('#');
        if (hashIdx >= 0) { frag = body[(hashIdx + 1)..]; body = body[..hashIdx]; }
        frag = UnquotePlain(frag).Trim();

        var qs = "";
        var qIdx = body.IndexOf('?');
        if (qIdx >= 0) { qs = body[(qIdx + 1)..]; body = body[..qIdx]; }

        if (!body.Contains('@')) { Die("в ссылке нет части «UUID@хост:порт»"); }
        var atIdx = body.LastIndexOf('@');
        var userinfo = body[..atIdx];
        var hostport = body[(atIdx + 1)..];
        var uuid = UnquotePlain(userinfo).Trim();
        var (host, portStr) = SplitHostPort(hostport);
        if (uuid.Length == 0) { Die("в ссылке не указан UUID"); }
        if (host.Length == 0) { Die("в ссылке не указан адрес сервера"); }

        var portToParse = portStr.Length == 0 ? "443" : portStr;
        if (!int.TryParse(portToParse, out var port)) { Die($"некорректный порт: {portStr}"); }
        if (port is < 1 or > 65535) { Die($"порт вне диапазона: {port}"); }

        var q = ParseQuery(qs);

        var net = (Get(q, "type") is { Length: > 0 } t1 ? t1 : Get(q, "network")).Trim().ToLowerInvariant();
        if (net.Length == 0) net = "tcp";
        if (net is "h2" or "http2") net = "http";
        if (!Networks.Contains(net))
        {
            Die($"неизвестный транспорт type={net} (поддерживаются: {NetworksSortedForError})");
        }

        var security = (Get(q, "security") is { Length: > 0 } sec ? sec : "none").Trim().ToLowerInvariant();
        if (security is "" or "auto" or "none") security = "none";
        if (security == "xtls") security = "tls"; // legacy xtls-rprx-direct больше не существует
        if (security is not ("none" or "tls" or "reality"))
        {
            Die($"неизвестный security={security} (ожидается none, tls или reality)");
        }

        var alpn = CommaOrWhitespaceRegex().Split(Get(q, "alpn")).Where(a => a.Length > 0).ToArray();

        var allowInsecureRaw = Get(q, "allowinsecure").Trim().ToLowerInvariant();
        var muxRaw = Get(q, "mux").Trim().ToLowerInvariant();

        var p = new ParsedLink
        {
            Link = link,
            Name = frag,
            Uuid = uuid,
            Host = host,
            Port = port,
            HostIsIp = IsIp(host),
            Network = net,
            Security = security,
            Encryption = TrimOrDefault(Get(q, "encryption"), "none"),
            Flow = Get(q, "flow").Trim(),
            Sni = Get(q, "sni").Trim(),
            Alpn = alpn,
            Fp = Get(q, "fp").Trim(),
            Pbk = Get(q, "pbk").Trim(),
            Sid = Get(q, "sid").Trim(),
            Spx = Get(q, "spx").Trim(),
            Pqv = Get(q, "pqv").Trim(),
            Path = Get(q, "path").Trim(),
            HostHeader = Get(q, "host").Trim(),
            Service = Get(q, "servicename").Trim(),
            Mode = Get(q, "mode").Trim().ToLowerInvariant(),
            HeaderType = Get(q, "headertype").Trim().ToLowerInvariant(),
            QuicSecurity = TrimOrDefault(Get(q, "quicsecurity"), "none"),
            Key = Get(q, "key").Trim(),
            Seed = Get(q, "seed").Trim(),
            Extra = Get(q, "extra").Trim(),
            AllowInsecure = allowInsecureRaw is "1" or "true" or "yes" or "on",
            Ech = Get(q, "ech").Trim(),
            Mux = muxRaw is "1" or "true" or "yes" or "on",
            Query = q,
        };

        if (security == "reality")
        {
            if (p.Pbk.Length == 0) { Die("в ссылке security=reality, но не указан publicKey (pbk)"); }
            if (net is not ("tcp" or "raw" or "http" or "xhttp" or "splithttp" or "grpc"))
            {
                Die($"REALITY не поддерживается с транспортом {net} (только tcp, xhttp, h2, grpc)");
            }
        }
        if (p.Flow is not ("" or "xtls-rprx-vision" or "xtls-rprx-vision-udp443"))
        {
            Die($"неизвестный flow={p.Flow} (поддерживаются xtls-rprx-vision и xtls-rprx-vision-udp443)");
        }
        if (p.Flow.Length > 0 && security == "none")
        {
            Die($"flow={p.Flow} требует security=tls или security=reality");
        }

        return p;
    }
}
