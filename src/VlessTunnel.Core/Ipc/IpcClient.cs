using System.IO.Pipes;
using System.Text.Json;

namespace VlessTunnel.Core.Ipc;

/// <summary>
/// Клиент именованного канала \\.\pipe\vless-tunnel (план, 3.4) — общий
/// для CLI и трея, чтобы не дублировать разбор NDJSON-протокола и логику
/// подписки на события в двух местах. Каждый вызов открывает своё
/// соединение (короткоживущее для команд, долгоживущее для подписки) —
/// простая модель без пула, оправданная тем, что команды на туннель и
/// так редки (не polling).
/// </summary>
public sealed class IpcClient
{
    public async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", IpcCommands.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct);

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var line = await reader.ReadLineAsync(cts.Token)
            ?? throw new IOException("Служба закрыла соединение без ответа");
        return JsonSerializer.Deserialize<IpcResponse>(line) ?? throw new IOException("Не удалось разобрать ответ службы");
    }

    /// <summary>
    /// Подписка на события (план, 3.4: "трей не опрашивает службу по
    /// таймеру") — держит соединение открытым и вызывает <paramref
    /// name="onEvent"/> при каждом состоянии, пока не отменят <paramref
    /// name="ct"/> или не порвётся канал (например, служба перезапустилась —
    /// вызывающий код сам решает, переподключаться ли).
    /// </summary>
    public async Task SubscribeAsync(Action<TunnelStatus> onEvent, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", IpcCommands.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);

        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Cmd = IpcCommands.Subscribe }));

        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            TunnelStatus? status = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("Status", out var statusEl) && statusEl.ValueKind == JsonValueKind.Object)
                    status = statusEl.Deserialize<TunnelStatus>();
            }
            catch (JsonException) { continue; }
            if (status is not null) onEvent(status);
        }
    }
}
