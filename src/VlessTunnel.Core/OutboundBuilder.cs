using System.Text.Json;
using System.Text.Json.Nodes;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Core;

/// <summary>
/// Сборка outbound-блока config.json для Xray. Порт build_stream() +
/// соответствующей части build_config() из py_backend() (эталон, v1.2.9).
/// Инбаунды на Windows принципиально другие (TUN вместо dokodemo-door,
/// план 3.1) и сюда не входят — план явно требует паритет только outbound.
/// </summary>
public static class OutboundBuilder
{
    /// <summary>Xray с flow=xtls-rprx-vision сам отклоняет UDP/443 (QUIC).
    /// Суффикс -udp443 — чисто клиентский флаг (сервер видит xtls-rprx-vision),
    /// без него QUIC/HTTP-3 через туннель работать не будет.</summary>
    public static string EffectiveFlow(ParsedLink p, bool visionUdp443 = true) =>
        p.Flow == "xtls-rprx-vision" && visionUdp443 ? "xtls-rprx-vision-udp443" : p.Flow;

    public static JsonObject BuildStream(ParsedLink p, BuildOptions? options = null)
    {
        options ??= new BuildOptions();
        var net = p.Network == "raw" ? "tcp" : p.Network;
        var st = new JsonObject { ["network"] = net };
        var hostHdr = p.HostHeader.Length > 0 ? p.HostHeader : p.Sni;

        switch (net)
        {
            case "tcp":
                if (p.HeaderType == "http")
                {
                    var headers = new JsonObject();
                    if (p.HostHeader.Length > 0) headers["Host"] = new JsonArray(p.HostHeader);
                    var req = new JsonObject
                    {
                        ["path"] = new JsonArray(p.Path.Length > 0 ? p.Path : "/"),
                        ["headers"] = headers,
                    };
                    st["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = req } };
                }
                break;

            case "ws":
            {
                var s = new JsonObject();
                if (p.Path.Length > 0) s["path"] = p.Path;
                if (hostHdr.Length > 0) s["host"] = hostHdr;
                st["wsSettings"] = s;
                break;
            }

            case "grpc":
            {
                var s = new JsonObject();
                if (p.Service.Length > 0) s["serviceName"] = p.Service;
                if (p.Mode == "multi") s["multiMode"] = true;
                st["grpcSettings"] = s;
                break;
            }

            case "http":
            {
                var s = new JsonObject();
                if (hostHdr.Length > 0)
                {
                    var hosts = hostHdr.Split(',').Where(x => x.Length > 0).Select(x => (JsonNode?)x);
                    s["host"] = new JsonArray(hosts.ToArray());
                }
                if (p.Path.Length > 0) s["path"] = p.Path;
                st["httpSettings"] = s;
                break;
            }

            case "quic":
                st["quicSettings"] = new JsonObject
                {
                    ["security"] = p.QuicSecurity.Length > 0 ? p.QuicSecurity : "none",
                    ["key"] = p.Key,
                    ["header"] = new JsonObject { ["type"] = p.HeaderType.Length > 0 ? p.HeaderType : "none" },
                };
                break;

            case "kcp":
            {
                var s = new JsonObject();
                if (p.HeaderType is { Length: > 0 } and not "none") s["header"] = new JsonObject { ["type"] = p.HeaderType };
                if (p.Seed.Length > 0) s["seed"] = p.Seed;
                st["kcpSettings"] = s;
                break;
            }

            case "httpupgrade":
            {
                var s = new JsonObject();
                if (p.Path.Length > 0) s["path"] = p.Path;
                if (hostHdr.Length > 0) s["host"] = hostHdr;
                st["httpupgradeSettings"] = s;
                break;
            }

            case "xhttp" or "splithttp":
            {
                var s = new JsonObject();
                if (p.Path.Length > 0) s["path"] = p.Path;
                if (hostHdr.Length > 0) s["host"] = hostHdr;
                if (p.Mode.Length > 0) s["mode"] = p.Mode;
                if (p.Extra.Length > 0)
                {
                    try
                    {
                        var extraNode = JsonNode.Parse(p.Extra);
                        if (extraNode is JsonObject) s["extra"] = extraNode;
                    }
                    catch (JsonException)
                    {
                        // предупреждение (не фатально) — как в эталоне: параметр extra
                        // не разобран как JSON, пропущен. Логирование — забота вызывающей стороны.
                    }
                }
                st[net == "xhttp" ? "xhttpSettings" : "splithttpSettings"] = s;
                break;
            }
        }

        if (p.Security == "reality")
        {
            var rs = new JsonObject
            {
                ["serverName"] = p.Sni.Length > 0 ? p.Sni : (p.HostHeader.Length > 0 ? p.HostHeader : p.Host),
                ["fingerprint"] = p.Fp.Length > 0 ? p.Fp : "chrome",
                ["publicKey"] = p.Pbk,
                ["shortId"] = p.Sid,
                ["spiderX"] = p.Spx.Length > 0 ? p.Spx : "/",
            };
            if (p.Pqv.Length > 0) rs["mldsa65Verify"] = p.Pqv;
            st["security"] = "reality";
            st["realitySettings"] = rs;
        }
        else if (p.Security == "tls")
        {
            var ts = new JsonObject
            {
                ["serverName"] = p.Sni.Length > 0 ? p.Sni : (p.HostHeader.Length > 0 ? p.HostHeader : p.Host),
            };
            if (p.Fp.Length > 0) ts["fingerprint"] = p.Fp;
            if (p.Alpn.Count > 0) ts["alpn"] = new JsonArray(p.Alpn.Select(a => (JsonNode?)a).ToArray());
            if (p.AllowInsecure)
            {
                if (options.CoreFamily == "legacy") ts["allowInsecure"] = true;
                else if (!string.IsNullOrEmpty(options.CertPin)) ts["pinnedPeerCertSha256"] = options.CertPin;
                // иначе — в эталоне это предупреждение в stderr, не meняет config; здесь тихо (лог — забота Service).
            }
            if (p.Ech.Length > 0) ts["echConfigList"] = p.Ech;
            st["security"] = "tls";
            st["tlsSettings"] = ts;
        }
        else
        {
            st["security"] = "none";
        }

        return st;
    }

    public static JsonObject BuildOutbound(ParsedLink p, BuildOptions? options = null)
    {
        options ??= new BuildOptions();
        var stream = BuildStream(p, options);
        var flow = EffectiveFlow(p, options.VisionUdp443);

        var user = new JsonObject
        {
            ["id"] = p.Uuid,
            ["encryption"] = p.Encryption.Length > 0 ? p.Encryption : "none",
        };
        if (flow.Length > 0) user["flow"] = flow;

        var outbound = new JsonObject
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = options.ServerAddressOverride ?? p.Host,
                    ["port"] = p.Port,
                    ["users"] = new JsonArray(user),
                }),
            },
            ["streamSettings"] = stream,
            ["mux"] = (options.Mux && flow.Length == 0)
                ? new JsonObject { ["enabled"] = true, ["concurrency"] = 8 }
                : new JsonObject { ["enabled"] = false },
        };

        return outbound;
    }
}
