namespace VlessTunnel.Core.Models;

/// <summary>
/// Разобранная vless:// ссылка. Один в один с полями словаря <c>p</c> из
/// py_backend()/parse_link в vless-tunnel.sh (эталон, v1.2.9) — порядок
/// полей и семантика сохранены намеренно, чтобы соответствие было легко
/// проверять построчно при обновлении эталона.
/// </summary>
public sealed record ParsedLink
{
    public required string Link { get; init; }
    public required string Name { get; init; }
    public required string Uuid { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool HostIsIp { get; init; }
    public required string Network { get; init; }
    public required string Security { get; init; }
    public required string Encryption { get; init; }
    public required string Flow { get; init; }
    public required string Sni { get; init; }
    public required IReadOnlyList<string> Alpn { get; init; }
    public required string Fp { get; init; }
    public required string Pbk { get; init; }
    public required string Sid { get; init; }
    public required string Spx { get; init; }
    public required string Pqv { get; init; }
    public required string Path { get; init; }
    public required string HostHeader { get; init; }
    public required string Service { get; init; }
    public required string Mode { get; init; }
    public required string HeaderType { get; init; }
    public required string QuicSecurity { get; init; }
    public required string Key { get; init; }
    public required string Seed { get; init; }
    public required string Extra { get; init; }
    public required bool AllowInsecure { get; init; }
    public required string Ech { get; init; }
    public required bool Mux { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
}
