using System.Text.Json.Nodes;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Core;

/// <summary>
/// Сборка полного config.json для Windows-клиента (план, 3.1/3.2).
/// В отличие от <see cref="OutboundBuilder"/>, паритет с Linux тут не
/// требуется и не проверяется golden-тестами — инбаунды принципиально
/// другие (TUN вместо dokodemo-door+TPROXY, план 3.1 "Отличия от Linux"),
/// поэтому и DNS/routing вокруг них отличаются: анти-петлевая защита на
/// Windows живёт на уровне маршрутов ОС (3.2, подтверждено спайком —
/// docs/spike.md), а не в routing-правилах Xray, как на Linux, поэтому
/// правил "адрес сервера -> direct" здесь нет вовсе. Домен сервера
/// резолвится службой ДО сборки конфига (bootstrap-резолв, 3.2, п.1) —
/// у Xray нет своего dns-direct-N для домена сервера, только DoH-резолверы
/// для трафика приложений.
/// </summary>
public static class ConfigBuilder
{
    // Тот же список приватных сетей, что и в эталоне (vless-tunnel.sh,
    // PRIVATE_CIDRS_V4/V6) — общий источник истины с Linux-версией, чтобы
    // не разъезжаться в дальнейшем.
    private static readonly string[] Private4 =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
        "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15",
        "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4",
    ];

    private static readonly string[] Private6 = ["::1/128", "fc00::/7", "fe80::/10", "ff00::/8"];

    private static JsonObject Sniffing() => new()
    {
        ["enabled"] = true,
        ["destOverride"] = new JsonArray("http", "tls", "quic"),
        ["routeOnly"] = false,
    };

    public static JsonObject BuildTunInbound(WindowsConfigOptions o)
    {
        var addresses = new JsonArray(o.TunAddressV4);
        if (o.TunAddressV6 is { Length: > 0 }) addresses.Add(o.TunAddressV6);

        return new JsonObject
        {
            ["tag"] = "tun-in",
            ["protocol"] = "tun",
            ["settings"] = new JsonObject
            {
                ["name"] = o.TunAdapterName,
                ["address"] = addresses,
                ["mtu"] = o.Mtu,
                // Служба сама ставит адрес/маршруты (план, 3.2) — Xray
                // не должен трогать таблицу маршрутов сам (подтверждено
                // спайком: autoRoute добавляет только узкие link-local
                // маршруты, не общий default-through-tun, и нам это не
                // нужно — конфликтовало бы с тем, что делает служба).
                ["autoRoute"] = false,
                ["strictRoute"] = false,
                ["stack"] = o.TunStack,
                ["endpointIndependentNat"] = false,
            },
            ["sniffing"] = Sniffing(),
        };
    }

    public static JsonArray BuildInbounds(WindowsConfigOptions o) =>
    [
        BuildTunInbound(o),
        new JsonObject
        {
            ["tag"] = "socks-in",
            ["listen"] = "127.0.0.1",
            ["port"] = o.SocksPort,
            ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true, ["address"] = "127.0.0.1" },
            ["sniffing"] = Sniffing(),
        },
        new JsonObject
        {
            ["tag"] = "http-in",
            ["listen"] = "127.0.0.1",
            ["port"] = o.HttpPort,
            ["protocol"] = "http",
            ["settings"] = new JsonObject { ["allowTransparent"] = false },
            ["sniffing"] = Sniffing(),
        },
    ];

    public static JsonArray BuildOutbounds(ParsedLink p, WindowsConfigOptions o) =>
    [
        OutboundBuilder.BuildOutbound(p, o.Outbound),
        new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["domainStrategy"] = "UseIP" } },
        new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole", ["settings"] = new JsonObject() },
        new JsonObject { ["tag"] = "dns-out", ["protocol"] = "dns", ["settings"] = new JsonObject() },
    ];

    public static JsonObject BuildDns()
    {
        var servers = new JsonArray(
            new JsonObject { ["tag"] = "dns-proxy-1", ["address"] = "https://1.1.1.1/dns-query", ["skipFallback"] = false },
            new JsonObject { ["tag"] = "dns-proxy-2", ["address"] = "https://8.8.8.8/dns-query", ["skipFallback"] = false });
        return new JsonObject { ["servers"] = servers, ["queryStrategy"] = "UseIP", ["disableFallback"] = false };
    }

    public static JsonArray BuildRoutingRules(WindowsConfigOptions o)
    {
        var rules = new JsonArray
        {
            // 1. DNS-запросы самого встроенного DNS Xray (DoH-резолверы) — через туннель.
            new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("dns-proxy-1", "dns-proxy-2"), ["outboundTag"] = "proxy" },
            // 2. Все DNS-запросы приложений (порт 53) — во встроенный DNS Xray, без утечек.
            new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns-out" },
        };

        if (o.ExcludeLan)
        {
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray(Private4.Concat(Private6).Select(x => (JsonNode?)x).ToArray()), ["outboundTag"] = "direct" });
        }

        rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" });
        return rules;
    }

    public static JsonObject BuildLog(WindowsConfigOptions o)
    {
        var log = new JsonObject { ["loglevel"] = o.LogLevel, ["dnsLog"] = false };
        if (o.AccessLog)
        {
            log["access"] = Path.Combine(o.LogDir, "access.log").Replace('\\', '/');
            log["error"] = Path.Combine(o.LogDir, "error.log").Replace('\\', '/');
        }
        return log;
    }

    public static JsonObject BuildConfig(ParsedLink p, WindowsConfigOptions o) => new()
    {
        ["log"] = BuildLog(o),
        ["dns"] = BuildDns(),
        ["inbounds"] = BuildInbounds(o),
        ["outbounds"] = BuildOutbounds(p, o),
        ["routing"] = new JsonObject { ["domainStrategy"] = "IPIfNonMatch", ["rules"] = BuildRoutingRules(o) },
    };
}
