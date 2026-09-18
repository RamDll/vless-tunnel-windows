using Xunit;

namespace VlessTunnel.Core.Tests;

public sealed class RedactTests
{
    [Fact]
    public void Redacts_uuid_anywhere_in_text()
    {
        var text = "config error near user a1b2c3d4-e5f6-4789-a012-b3c4d5e6f789 at line 3";
        var result = Redact.Secrets(text);
        Assert.DoesNotContain("a1b2c3d4-e5f6-4789-a012-b3c4d5e6f789", result);
        Assert.Contains("«скрыто»", result);
        Assert.Contains("at line 3", result);
    }

    [Fact]
    public void Leaves_text_without_uuid_untouched()
    {
        var text = "from tcp:172.19.0.1:59503 accepted tcp:1.1.1.1:443 [tun-in -> proxy]";
        Assert.Equal(text, Redact.Secrets(text));
    }
}
