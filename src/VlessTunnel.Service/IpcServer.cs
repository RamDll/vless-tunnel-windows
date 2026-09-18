using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Service;

/// <summary>
/// Сервер именованного канала (план, 3.4): <c>\\.\pipe\vless-tunnel</c>,
/// ACL — SYSTEM, Administrators и (опционально) один настроенный
/// пользователь (аналог <c>--allow-user</c>); протокол — JSON-строки,
/// запрос/ответ + подписка на события в рамках того же соединения.
/// Несколько клиентов обслуживаются параллельно (каждое соединение —
/// свой Task), но все команды на туннель сериализуются внутри
/// <see cref="TunnelController"/> одним <c>SemaphoreSlim</c>.
/// </summary>
public sealed class IpcServer
{
    private readonly TunnelController _controller;
    private readonly Action<string> _log;
    private readonly string? _allowedUser;

    // Активные подписчики на события (план, 3.4: "трей не опрашивает
    // службу по таймеру") — рассылка при каждой смене состояния.
    private readonly List<StreamWriter> _subscribers = [];
    private readonly Lock _subscribersLock = new();

    public IpcServer(TunnelController controller, Action<string> log, string? allowedUser = null)
    {
        _controller = controller;
        _log = log;
        _allowedUser = allowedUser;
        _controller.StatusChanged += Broadcast;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }
            _ = HandleClientAsync(pipe, ct);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        if (_allowedUser is { Length: > 0 })
        {
            var sid = (SecurityIdentifier)new NTAccount(_allowedUser).Translate(typeof(SecurityIdentifier));
            security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            IpcCommands.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        StreamWriter? subscriberWriter = null;
        try
        {
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                IpcRequest? req;
                try { req = JsonSerializer.Deserialize<IpcRequest>(line); }
                catch (JsonException) { await WriteAsync(writer, new IpcResponse { Ok = false, Error = "bad json" }); continue; }
                if (req is null) continue;

                if (req.Cmd == IpcCommands.Subscribe)
                {
                    subscriberWriter = writer;
                    lock (_subscribersLock) { _subscribers.Add(writer); }
                    await WriteAsync(writer, new IpcResponse { Ok = true, Status = _controller.GetStatus() });
                    continue; // клиент остаётся на связи и получает события, но может слать и новые команды
                }

                var resp = await DispatchAsync(req, ct);
                await WriteAsync(writer, resp);
            }
        }
        catch (Exception ex)
        {
            _log($"IPC-соединение упало: {ex.Message}");
        }
        finally
        {
            if (subscriberWriter is not null)
                lock (_subscribersLock) { _subscribers.Remove(subscriberWriter); }
            pipe.Dispose();
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest req, CancellationToken ct)
    {
        try
        {
            switch (req.Cmd)
            {
                case IpcCommands.Status:
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.SetLink:
                    if (req.Link is null) return new IpcResponse { Ok = false, Error = "link required" };
                    _controller.SetLink(req.Link);
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.On:
                    await _controller.OnAsync(ct);
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.Off:
                    await _controller.OffAsync();
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.Toggle:
                    await _controller.ToggleAsync(ct);
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.Restart:
                    await _controller.RestartAsync(ct);
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.Doctor:
                    var removed = Native.KillSwitch.Doctor(s => _log($"doctor: {s}"));
                    var clock = await VlessTunnel.Core.ClockCheck.CheckAsync(ct: ct);
                    return new IpcResponse
                    {
                        Ok = true,
                        DoctorRemoved = removed,
                        DoctorClockSkewSeconds = clock.SkewSeconds,
                        DoctorClockError = clock.Error,
                    };
                case IpcCommands.Test:
                    var results = await TunnelTester.RunAsync();
                    return new IpcResponse { Ok = true, Test = results };
                case IpcCommands.CaptivePortal:
                    await _controller.CaptivePortalBypassAsync(TimeSpan.FromMinutes(5));
                    return new IpcResponse { Ok = true, Status = _controller.GetStatus() };
                case IpcCommands.UpdateCore:
                    var coreVersion = await _controller.UpdateCoreAsync(ct);
                    return new IpcResponse { Ok = true, UpdatedToVersion = coreVersion };
                case IpcCommands.SelfUpdate:
                    // Как и update-core — пока активен kill-switch, самому
                    // процессу службы (не xray.exe) выйти в интернет за
                    // релизом тоже нельзя (живой тест update-core). Сам
                    // установщик всё равно перезапустит службу с нуля —
                    // включать туннель обратно после незачем.
                    await _controller.OffAsync();
                    var appVersion = await SelfUpdater.CheckAndRunAsync(_log, ct);
                    return new IpcResponse { Ok = true, UpdatedToVersion = appVersion };
                default:
                    return new IpcResponse { Ok = false, Error = $"unknown command: {req.Cmd}" };
            }
        }
        catch (Exception ex)
        {
            return new IpcResponse { Ok = false, Error = ex.Message };
        }
    }

    private void Broadcast(TunnelStatus status)
    {
        var ev = JsonSerializer.Serialize(new IpcEvent { Event = "state-changed", Status = status });
        List<StreamWriter> targets;
        lock (_subscribersLock) { targets = [.. _subscribers]; }
        foreach (var w in targets)
        {
            try { w.WriteLine(ev); }
            catch { /* отключившийся подписчик уберётся при следующем HandleClientAsync.finally */ }
        }
    }

    private static Task WriteAsync(StreamWriter writer, IpcResponse resp) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(resp));
}
