namespace VlessTunnel.Core.Models;

/// <summary>
/// Опции сборки ПОЛНОГО config.json для Windows (план, 3.1/3.2) — шире
/// <see cref="BuildOptions"/>, который отвечает только за outbound-блок
/// ради паритета с Linux. Здесь всё специфичное для Windows-инбаундов
/// (TUN/SOCKS/HTTP) и routing, паритета с Linux не требуется и не
/// проверяется (инбаунды принципиально другие — TUN вместо dokodemo-door,
/// см. 3.1 "Отличия от Linux").
/// </summary>
public sealed class WindowsConfigOptions
{
    public required BuildOptions Outbound { get; init; }

    public int SocksPort { get; init; } = 10808;
    public int HttpPort { get; init; } = 10809;

    /// <summary>Приватные сети остаются на физическом интерфейсе на уровне
    /// ОС (план, 3.2, п.6) — это правило в routing Xray дублирует ту же
    /// логику как defense-in-depth, на случай если такой трафик всё же
    /// попадёт в TUN-инбаунд. Отключается опцией --proxy-lan.</summary>
    public bool ExcludeLan { get; init; } = true;

    public string LogLevel { get; init; } = "warning";
    public bool AccessLog { get; init; }
    public string LogDir { get; init; } = "";

    /// <summary>IPv4-адрес TUN-адаптера в нотации CIDR. Служба назначает
    /// его отдельно через Windows API (New-NetIPAddress/CreateUnicastIpAddressEntry) —
    /// это поле конфига Xray самим адресом адаптер НЕ снабжает (подтверждено
    /// спайком, docs/spike.md), но должно совпадать с тем, что реально
    /// назначает служба.</summary>
    public string TunAddressV4 { get; init; } = "172.19.0.1/30";

    /// <summary>ULA-адрес для IPv6 (план, 3.2: "для IPv6 — ULA из fd00::/8,
    /// без маршрутизируемого адреса IPv6 через TUN не пойдёт" — подтверждено
    /// спайком). Null — IPv6 в TUN не поднимается (--block-ipv6).</summary>
    public string? TunAddressV6 { get; init; } = "fd00::1/64";

    public int Mtu { get; init; } = 1400;

    /// <summary>"gvisor" | "system" — спайк не показал разницы в поведении
    /// (docs/spike.md), оставлено "gvisor" как изначально проверенное.</summary>
    public string TunStack { get; init; } = "gvisor";

    public string TunAdapterName { get; init; } = "xray0";

    /// <summary>DNS-серверы для самого TUN-адаптера (не для Xray — те же
    /// DoH-адреса уже прописаны в dns-секции ConfigBuilder для внутреннего
    /// резолва Xray). Без этого у Windows буквально некого спросить для
    /// доменов, ушедших в TUN по /1-маршрутам — раньше не назначались
    /// вовсе (найдено на реальной машине тестировщика: сырой TCP по IP
    /// через туннель работал, а разрешение имён отваливалось целиком).</summary>
    public string[] DnsServers { get; init; } = ["1.1.1.1", "8.8.8.8"];
}
