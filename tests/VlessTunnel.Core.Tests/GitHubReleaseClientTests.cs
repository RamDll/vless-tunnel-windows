using System.Net;
using VlessTunnel.Core;
using Xunit;

namespace VlessTunnel.Core.Tests;

file sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

public class GitHubReleaseClientTests
{
    private const string SampleReleaseJson = """
        {
          "tag_name": "v1.2.3",
          "assets": [
            { "name": "vless-tunnel-setup.exe", "browser_download_url": "https://example.com/vless-tunnel-setup.exe" },
            { "name": "vless-tunnel-setup.exe.sha256", "browser_download_url": "https://example.com/vless-tunnel-setup.exe.sha256" }
          ]
        }
        """;

    [Fact]
    public async Task GetLatestAsync_ParsesTagAndAssets()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleReleaseJson),
        }));

        var release = await GitHubReleaseClient.GetLatestAsync("RamDll", "vless-tunnel-windows", client);

        Assert.Equal("v1.2.3", release.TagName);
        Assert.Equal(2, release.Assets.Count);
        Assert.Contains(release.Assets, a => a.Name == "vless-tunnel-setup.exe");
    }

    [Fact]
    public async Task GetLatestAsync_SendsUserAgent()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new StubHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleReleaseJson) };
        }));

        await GitHubReleaseClient.GetLatestAsync("RamDll", "vless-tunnel-windows", client);

        Assert.NotNull(captured);
        Assert.NotEmpty(captured!.Headers.UserAgent);
    }

    [Fact]
    public async Task DownloadToFileAsync_WritesContentToDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt-dl-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5]),
            }));
            var dest = Path.Combine(dir, "file.bin");

            await GitHubReleaseClient.DownloadToFileAsync("https://example.com/file.bin", dest, client);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, await File.ReadAllBytesAsync(dest));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Sha256HexAsync_MatchesKnownHash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vt-sha-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "hello.txt");
            await File.WriteAllTextAsync(path, "hello");

            var hash = await GitHubReleaseClient.Sha256HexAsync(path);

            // sha256("hello") — известное эталонное значение.
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", hash);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("SHA2-256= abc123\n", "abc123")]
    [InlineData("MD5= aaa\nSHA1= bbb\nSHA2-256= ddd456\nSHA2-512= eee\n", "ddd456")]
    [InlineData("MD5= aaa\n", null)]
    public void ParseSha256FromDgst_ExtractsExpectedLine(string dgst, string? expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.ParseSha256FromDgst(dgst));
    }
}
