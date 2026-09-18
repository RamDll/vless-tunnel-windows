namespace VlessTunnel.Core.Ipc;

/// <summary>
/// Протокол IPC (план, 3.4): именованный канал <c>\\.\pipe\vless-tunnel</c>,
/// JSON-строки (по одному объекту на строку, NDJSON) — запрос/ответ и
/// подписка на события в рамках одного и того же соединения. Лежит в Core,
/// а не в Service/Cli, потому что это общий контракт, который используют
/// оба конца канала и который не завязан на Windows-специфику самого
/// транспорта (в отличие от ACL именованного канала — это уже на стороне
/// сервера, VlessTunnel.Service).
/// </summary>
public static class IpcCommands
{
    public const string PipeName = "vless-tunnel";
    public const string On = "on";
    public const string Off = "off";
    public const string Toggle = "toggle";
    public const string Restart = "restart";
    public const string Status = "status";
    public const string SetLink = "set-link";
    public const string Subscribe = "subscribe";
    public const string Doctor = "doctor";
    public const string Test = "test";
    public const string CaptivePortal = "captive-portal";
    public const string UpdateCore = "update-core";
    public const string SelfUpdate = "self-update";
}

public sealed class IpcRequest
{
    public required string Cmd { get; init; }

    /// <summary>Только для set-link.</summary>
    public string? Link { get; init; }
}

public sealed class IpcResponse
{
    public required bool Ok { get; init; }
    public string? Error { get; init; }
    public TunnelStatus? Status { get; init; }
    public int? DoctorRemoved { get; init; }
    public double? DoctorClockSkewSeconds { get; init; }
    public string? DoctorClockError { get; init; }
    public TestResults? Test { get; init; }
    public string? UpdatedToVersion { get; init; }
}

/// <summary>Результаты `test` (план, 3.5) — HTTP/SOCKS5/прозрачный TCP/DNS.</summary>
public sealed class TestResults
{
    public required bool Http { get; init; }
    public required bool Socks5 { get; init; }
    public required bool TransparentTcp { get; init; }
    public required bool Dns { get; init; }
}

/// <summary>Асинхронное событие подписки (план, 3.4: "трей не опрашивает службу по таймеру").</summary>
public sealed class IpcEvent
{
    public required string Event { get; init; } = "state-changed";
    public required TunnelStatus Status { get; init; }
}

public enum TunnelState
{
    Off,
    Starting,
    On,
    Stopping,
    Error,
}

public sealed class TunnelStatus
{
    public required TunnelState State { get; init; }
    public string? ServerHost { get; init; }
    public int? ServerPort { get; init; }
    public string? Network { get; init; }
    public string? Security { get; init; }
    public string? CoreVersion { get; init; }
    public int SocksPort { get; init; } = 10808;
    public int HttpPort { get; init; } = 10809;
    public bool AutostartEnabled { get; init; }
    public string? Error { get; init; }
}
