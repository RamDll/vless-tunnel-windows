using VlessTunnel.Core;
using Xunit;

namespace VlessTunnel.Core.Tests;

/// <summary>
/// Точечные тесты на «текстовые» причуды парсера, которые не покрываются
/// golden-тестами outbound-блока (ParityTests) — сама конструкция ссылки,
/// а не то, что из неё строится.
/// </summary>
public sealed class LinkParserTests
{
    private const string ValidLink =
        "vless://00000000-0000-4000-8000-000000000001@203.0.113.10:443?security=reality&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=deadbeef01234567&sni=example.com&type=tcp#name";

    [Fact]
    public void Link_without_scheme_gets_vless_prefix()
    {
        var noScheme = "00000000-0000-4000-8000-000000000001@203.0.113.10:443?type=tcp";
        var p = LinkParser.Parse(noScheme);
        Assert.Equal("203.0.113.10", p.Host);
        Assert.Equal(443, p.Port);
    }

    [Fact]
    public void Extra_prompt_text_before_link_is_stripped()
    {
        var withPrompt = "user@host:~$ " + ValidLink;
        var p = LinkParser.Parse(withPrompt);
        Assert.Equal("203.0.113.10", p.Host);
    }

    [Fact]
    public void Link_broken_by_line_wrap_is_reassembled()
    {
        // Склейка продолжения работает, только если хвост после переноса
        // начинается с буквы/цифры (эталон намеренно отсекает хвосты вида
        // "== ..." / "--- ..." — см. комментарий в py_backend/parse_link).
        // Перенос сразу ПОСЛЕ "&" (значит "&" достаётся первому токену, а
        // второй начинается с буквы) — со стороны текста ссылка цела.
        var wrapped = "vless://00000000-0000-4000-8000-000000000001@203.0.113.10:443?security=reality&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&\nsid=deadbeef01234567&sni=example.com&type=tcp#name";
        var p = LinkParser.Parse(wrapped);
        Assert.Equal("example.com", p.Sni);
        Assert.Equal("tcp", p.Network);
    }

    [Fact]
    public void Link_wrapped_right_before_ampersand_is_not_reassembled()
    {
        // Задокументированное поведение эталона: разрыв прямо перед "&"
        // НЕ склеивается (хвост не начинается с буквы/цифры) — сознательно
        // не "чиним" то, что реальный клиент тоже не чинит.
        var wrapped = "vless://00000000-0000-4000-8000-000000000001@203.0.113.10:443?security=reality&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=deadbeef01234567\n&sni=example.com&type=tcp#name";
        var p = LinkParser.Parse(wrapped);
        Assert.Equal("", p.Sni);
    }

    [Fact]
    public void Ipv6_host_without_port_uses_default_443()
    {
        var link = "vless://00000000-0000-4000-8000-000000000001@[2001:db8::1]?type=tcp";
        var p = LinkParser.Parse(link);
        Assert.Equal("2001:db8::1", p.Host);
        Assert.Equal(443, p.Port);
        Assert.True(p.HostIsIp);
    }

    [Fact]
    public void Domain_host_is_not_flagged_as_ip()
    {
        var p = LinkParser.Parse(ValidLink.Replace("203.0.113.10", "example.com"));
        Assert.False(p.HostIsIp);
    }

    [Fact]
    public void Empty_link_throws()
    {
        var ex = Assert.Throws<LinkParseException>(() => LinkParser.Parse(""));
        Assert.Contains("пустая", ex.Message);
    }

    [Fact]
    public void Null_link_throws()
    {
        Assert.Throws<LinkParseException>(() => LinkParser.Parse(null));
    }

    [Fact]
    public void Whitespace_only_encryption_falls_back_to_none()
    {
        var link = ValidLink + "&encryption=%20%20";
        var p = LinkParser.Parse(link);
        Assert.Equal("none", p.Encryption);
    }

    [Fact]
    public void Fragment_name_is_url_decoded()
    {
        var link = ValidLink.Replace("#name", "#%D0%BF%D1%80%D0%B8%D0%B2%D0%B5%D1%82");
        var p = LinkParser.Parse(link);
        Assert.Equal("привет", p.Name);
    }
}
