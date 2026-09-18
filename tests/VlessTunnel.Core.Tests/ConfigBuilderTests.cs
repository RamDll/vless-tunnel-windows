using System.Text.Json.Nodes;
using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using Xunit;

namespace VlessTunnel.Core.Tests;

/// <summary>
/// Тесты полного config.json для Windows — не golden (инбаунды у Windows
/// принципиально другие, план 3.1 не требует паритета с Linux здесь),
/// а поведенческие: проверяют требования плана напрямую (3.1/3.2).
/// </summary>
public sealed class ConfigBuilderTests
{
    private const string Link =
        "vless://00000000-0000-4000-8000-000000000001@example.com:443?security=reality&fp=chrome&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=deadbeef01234567&sni=example.com&type=tcp#name";

    private static ParsedLink Parse() => LinkParser.Parse(Link);

    [Fact]
    public void Tun_inbound_has_autoRoute_false_and_expected_shape()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var tun = ConfigBuilder.BuildTunInbound(o);

        Assert.Equal("tun", (string)tun["protocol"]!);
        var settings = tun["settings"]!;
        Assert.False((bool)settings["autoRoute"]!);
        Assert.Equal("172.19.0.1/30", (string)settings["address"]![0]!);
        Assert.Equal("fd00::1/64", (string)settings["address"]![1]!);
        Assert.Equal(1400, (int)settings["mtu"]!);
        Assert.Equal("xray0", (string)settings["name"]!);
    }

    [Fact]
    public void Ipv6_address_omitted_when_null()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions(), TunAddressV6 = null };
        var tun = ConfigBuilder.BuildTunInbound(o);
        Assert.Single(tun["settings"]!["address"]!.AsArray());
    }

    [Fact]
    public void Inbounds_are_tun_socks_http_in_that_order()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var inbounds = ConfigBuilder.BuildInbounds(o);
        Assert.Equal(3, inbounds.Count);
        Assert.Equal("tun-in", (string)inbounds[0]!["tag"]!);
        Assert.Equal("socks-in", (string)inbounds[1]!["tag"]!);
        Assert.Equal(10808, (int)inbounds[1]!["port"]!);
        Assert.True((bool)inbounds[1]!["settings"]!["udp"]!); // SOCKS5 с UDP, как на Linux (план 3.1)
        Assert.Equal("http-in", (string)inbounds[2]!["tag"]!);
        Assert.Equal(10809, (int)inbounds[2]!["port"]!);
    }

    [Fact]
    public void Routing_has_no_server_ip_direct_rule_unlike_linux()
    {
        // План 3.2: анти-петля на Windows — маршрут ОС (хост-роут через
        // физический шлюз), не правило Xray. Подтверждено спайком.
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var rules = ConfigBuilder.BuildRoutingRules(o);
        foreach (var rule in rules)
        {
            Assert.False(rule!.AsObject().ContainsKey("domain"));
        }
    }

    [Fact]
    public void Routing_port_53_goes_to_dns_out()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var rules = ConfigBuilder.BuildRoutingRules(o);
        var portRule = rules.Single(r => r!.AsObject().ContainsKey("port"));
        Assert.Equal("53", (string)portRule!["port"]!);
        Assert.Equal("dns-out", (string)portRule["outboundTag"]!);
    }

    [Fact]
    public void ExcludeLan_true_adds_private_net_rule_before_catch_all()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions(), ExcludeLan = true };
        var rules = ConfigBuilder.BuildRoutingRules(o);
        var ipRuleIndex = rules.ToList().FindIndex(r => r!.AsObject().ContainsKey("ip"));
        var catchAllIndex = rules.ToList().FindIndex(r => (string?)r!["network"] == "tcp,udp");
        Assert.True(ipRuleIndex >= 0);
        Assert.True(ipRuleIndex < catchAllIndex);
        Assert.Equal("direct", (string)rules[ipRuleIndex]!["outboundTag"]!);
    }

    [Fact]
    public void ExcludeLan_false_omits_private_net_rule()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions(), ExcludeLan = false };
        var rules = ConfigBuilder.BuildRoutingRules(o);
        Assert.DoesNotContain(rules, r => r!.AsObject().ContainsKey("ip"));
    }

    [Fact]
    public void Catch_all_rule_sends_everything_to_proxy()
    {
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var rules = ConfigBuilder.BuildRoutingRules(o);
        var last = rules[^1]!;
        Assert.Equal("tcp,udp", (string)last["network"]!);
        Assert.Equal("proxy", (string)last["outboundTag"]!);
    }

    [Fact]
    public void Dns_has_only_doh_resolvers_no_server_domain_entry()
    {
        // Windows: сервер уже резолвится службой до сборки конфига (bootstrap,
        // план 3.2 п.1) — в отличие от Linux, тут нет dns-direct-N для домена сервера.
        var dns = ConfigBuilder.BuildDns();
        var servers = dns["servers"]!.AsArray();
        Assert.Equal(2, servers.Count);
        Assert.Equal("dns-proxy-1", (string)servers[0]!["tag"]!);
        Assert.Equal("dns-proxy-2", (string)servers[1]!["tag"]!);
    }

    [Fact]
    public void Server_address_override_is_used_in_vnext_when_provided()
    {
        var p = Parse();
        var o = new WindowsConfigOptions { Outbound = new BuildOptions { ServerAddressOverride = "203.0.113.55" } };
        var cfg = ConfigBuilder.BuildConfig(p, o);
        var proxy = cfg["outbounds"]![0]!;
        var vnext = proxy["settings"]!["vnext"]![0]!;
        Assert.Equal("203.0.113.55", (string)vnext["address"]!);
    }

    [Fact]
    public void Access_log_paths_only_present_when_enabled()
    {
        var oOff = new WindowsConfigOptions { Outbound = new BuildOptions(), AccessLog = false };
        var logOff = ConfigBuilder.BuildLog(oOff);
        Assert.False(logOff.ContainsKey("access"));

        var oOn = new WindowsConfigOptions { Outbound = new BuildOptions(), AccessLog = true, LogDir = "C:\\ProgramData\\vless-tunnel\\logs" };
        var logOn = ConfigBuilder.BuildLog(oOn);
        Assert.Equal("C:/ProgramData/vless-tunnel/logs/access.log", (string)logOn["access"]!);
    }

    [Fact]
    public void Full_config_has_all_top_level_sections()
    {
        var p = Parse();
        var o = new WindowsConfigOptions { Outbound = new BuildOptions() };
        var cfg = ConfigBuilder.BuildConfig(p, o);
        foreach (var key in new[] { "log", "dns", "inbounds", "outbounds", "routing" })
        {
            Assert.True(cfg.ContainsKey(key), $"missing section: {key}");
        }
    }
}
