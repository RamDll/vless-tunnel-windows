using VlessTunnel.Core;
using Xunit;

namespace VlessTunnel.Core.Tests;

/// <summary>
/// Ревью п.11 — LinkParser сам продолжает принимать quic/http/h2 (parity
/// с Linux-эталоном), это отдельная Windows-специфичная проверка ПОСЛЕ
/// разбора (план, раздел 1: "Не делаем").
/// </summary>
public sealed class WindowsTransportGuardTests
{
    [Theory]
    [InlineData("quic")]
    [InlineData("http")]
    public void Rejects_transports_without_tun_inbound(string network)
    {
        var link = LinkParser.Parse($"vless://11111111-1111-1111-1111-111111111111@example.com:443?type={network}");
        var ex = Assert.Throws<LinkParseException>(() => WindowsTransportGuard.RequireTunSupported(link));
        Assert.Contains(network, ex.Message);
    }

    [Fact]
    public void H2_is_normalized_to_http_by_the_parser_and_still_rejected()
    {
        // LinkParser.cs: net is "h2" or "http2" -> net = "http" — до
        // WindowsTransportGuard дело доходит уже с нормализованным именем.
        var link = LinkParser.Parse("vless://11111111-1111-1111-1111-111111111111@example.com:443?type=h2");
        Assert.Equal("http", link.Network);
        Assert.Throws<LinkParseException>(() => WindowsTransportGuard.RequireTunSupported(link));
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("ws")]
    [InlineData("grpc")]
    [InlineData("xhttp")]
    public void Allows_supported_transports(string network)
    {
        var link = LinkParser.Parse($"vless://11111111-1111-1111-1111-111111111111@example.com:443?type={network}");
        WindowsTransportGuard.RequireTunSupported(link); // не должно бросить
    }
}
