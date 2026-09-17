namespace VlessTunnel.Core.Models;

/// <summary>
/// Опции сборки outbound-блока — соответствуют части словаря <c>o</c> из
/// read_opts() в эталоне, но только тем полям, что влияют на outbound
/// (план требует побайтового паритета именно outbound-блока, 3.1). Опции,
/// специфичные для Linux (fwmark, rt_table, tproxy_port, svc_user...) сюда
/// не перенесены — на Windows их не существует.
/// </summary>
public sealed class BuildOptions
{
    /// <summary>flow=xtls-rprx-vision получает суффикс -udp443, если true
    /// (иначе ядро отбрасывает UDP/443 и QUIC/HTTP-3 через туннель не идёт).</summary>
    public bool VisionUdp443 { get; init; } = true;

    public bool Mux { get; init; }

    /// <summary>"modern" | "legacy" — влияет на обработку allowInsecure=1
    /// (в legacy-ядре разрешён прямой allowInsecure, в modern — только через
    /// pinnedPeerCertSha256, см. CertPin).</summary>
    public string CoreFamily { get; init; } = "modern";

    /// <summary>Отпечаток сертификата сервера (hex, через запятую можно
    /// несколько) для pinnedPeerCertSha256 при allowInsecure=1 и modern-ядре.</summary>
    public string? CertPin { get; init; }

    /// <summary>Windows-специфика (план, 3.2): служба резолвит домен сервера
    /// сама и подставляет уже разрешённый IP в vnext.address, SNI/Host
    /// остаются доменом. Для golden-тестов паритета с Linux оставлять null —
    /// Linux всегда использует исходный host как есть.</summary>
    public string? ServerAddressOverride { get; init; }
}
