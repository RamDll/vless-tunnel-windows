using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace VlessTunnel.Core;

/// <summary>
/// Общая часть для <c>update-core</c> и <c>self-update</c> (план, 3.9) —
/// узнать последний релиз на GitHub, скачать файл, посчитать sha256.
/// Никакой Windows-специфики здесь нет (это просто HTTP), поэтому в
/// Core, а не в Service — и, как и <see cref="ClockCheck"/>, тестируется
/// подставным <c>HttpMessageHandler</c>, без реальной сети.
/// </summary>
public static class GitHubReleaseClient
{
    public sealed record Asset(string Name, string DownloadUrl);
    public sealed record Release(string TagName, IReadOnlyList<Asset> Assets);

    public static async Task<Release> GetLatestAsync(string owner, string repo, HttpClient? client = null, CancellationToken ct = default)
    {
        var http = client ?? new HttpClient();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
            // GitHub API требует User-Agent на любой запрос, иначе 403.
            req.Headers.UserAgent.ParseAdd("vless-tunnel-windows");
            using var resp = await http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidOperationException("В ответе GitHub нет tag_name");
            var assets = new List<Asset>();
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name is not null && url is not null) assets.Add(new Asset(name, url));
            }
            return new Release(tag, assets);
        }
        finally
        {
            if (client is null) http.Dispose();
        }
    }

    public static async Task DownloadToFileAsync(string url, string destPath, HttpClient? client = null, CancellationToken ct = default)
    {
        var http = client ?? new HttpClient();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("vless-tunnel-windows");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(destPath);
            await src.CopyToAsync(dst, ct);
        }
        finally
        {
            if (client is null) http.Dispose();
        }
    }

    public static async Task<string> Sha256HexAsync(string path, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Разбирает .dgst-файл релизов Xray-core (план: "сверка sha256... с
    /// .dgst") — формат "SHA2-256= &lt;hex&gt;" построчно, тот же, что уже
    /// разбирает Linux-версия (<c>awk -F'= ' '/^SHA2-256/{print $2}'</c>).
    /// </summary>
    public static string? ParseSha256FromDgst(string dgstContent)
    {
        foreach (var line in dgstContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("SHA2-256", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = trimmed.IndexOf('=');
            if (eq < 0) continue;
            return trimmed[(eq + 1)..].Trim().ToLowerInvariant();
        }
        return null;
    }
}
