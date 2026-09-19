using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
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

    // Ревью п.8: раньше Broadcast писал подписчикам синхронно
    // (StreamWriter.WriteLine) прямо из SetState, который вызывается
    // ИЗНУТРИ OnAsync/OffAsync под единственным на всю службу _gate
    // (TunnelController) — если подписчик (например, зависший трей) не
    // читает свой конец канала, ОС-буфер именованного канала заполняется,
    // WriteLine блокируется НАВСЕГДА, _gate никогда не освобождается — то
    // есть один зависший клиент вешал вообще ВСЮ службу для всех.
    // Теперь у каждого подписчика своя ограниченная очередь (Channel) и
    // отдельная задача-обработчик — Broadcast только кладёт в очередь
    // (TryWrite, никогда не блокируется) и отключает подписчика, если тот
    // не успевает вычитывать (очередь переполнена).
    private sealed record Subscriber(StreamWriter Writer, Channel<string> Queue, NamedPipeServerStream Pipe);

    // Активные подписчики на события (план, 3.4: "трей не опрашивает
    // службу по таймеру") — рассылка при каждой смене состояния.
    private readonly List<Subscriber> _subscribers = [];
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
        Subscriber? subscriber = null;
        // Ревью п.16: фикс п.8 закрыл блокировку (Broadcast/TryWrite
        // никогда не ждёт), но не гонку — DrainSubscriberAsync ниже и цикл
        // запрос-ответ здесь пишут в ОДИН И ТОТ ЖЕ StreamWriter из двух
        // разных задач на одном соединении (ровно то, что делает трей:
        // подписывается и шлёт команды по одному и тому же pipe).
        // StreamWriter не потокобезопасен — событие и ответ на команду
        // могли перемешаться в одной строке NDJSON. Один SemaphoreSlim на
        // оба пути записи для ЭТОГО соединения (не на весь IpcServer —
        // другие соединения друг другу не мешают и не должны ждать).
        using var writeLock = new SemaphoreSlim(1, 1);
        try
        {
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                IpcRequest? req;
                try { req = JsonSerializer.Deserialize<IpcRequest>(line); }
                catch (JsonException) { await WriteAsync(writer, writeLock, new IpcResponse { Ok = false, Error = "bad json" }); continue; }
                if (req is null) continue;

                if (req.Cmd == IpcCommands.Subscribe)
                {
                    // Capacity=8 — с большим запасом на реальный темп смены
                    // состояний (доли герц), не на пропускную способность;
                    // переполнение означает "подписчик не читает вообще",
                    // не "события идут слишком часто".
                    var queue = Channel.CreateBounded<string>(8);
                    subscriber = new Subscriber(writer, queue, pipe);
                    lock (_subscribersLock) { _subscribers.Add(subscriber); }
                    _ = DrainSubscriberAsync(subscriber, writeLock, ct);
                    await WriteAsync(writer, writeLock, new IpcResponse { Ok = true, Status = _controller.GetStatus() });
                    continue; // клиент остаётся на связи и получает события, но может слать и новые команды
                }

                var resp = await DispatchAsync(req, ct);
                await WriteAsync(writer, writeLock, resp);
            }
        }
        catch (Exception ex)
        {
            _log($"IPC-соединение упало: {ex.Message}");
        }
        finally
        {
            if (subscriber is not null)
            {
                lock (_subscribersLock) { _subscribers.Remove(subscriber); }
                subscriber.Queue.Writer.TryComplete();
            }
            pipe.Dispose();
        }
    }

    // Один на подписчика — вычитывает очередь и пишет в pipe. Живёт,
    // пока очередь не завершится (клиент отключился, см. finally выше)
    // или пока сама запись не упадёт (пайп разорван клиентом раньше,
    // чем HandleClientAsync это заметил); в обоих случаях просто
    // завершается — Broadcast больше не найдёт этого подписчика в
    // списке при следующей рассылке (или найдёт, но TryWrite в уже
    // Complete-нутый Channel безопасно вернёт false). writeLock может
    // оказаться уже Dispose-нутым (HandleClientAsync завершился раньше,
    // чем эта задача заметила разрыв) — тогда WaitAsync/Release бросят
    // ObjectDisposedException, который ловит catch ниже, как и разрыв пайпа.
    private static async Task DrainSubscriberAsync(Subscriber sub, SemaphoreSlim writeLock, CancellationToken ct)
    {
        try
        {
            await foreach (var ev in sub.Queue.Reader.ReadAllAsync(ct))
            {
                await writeLock.WaitAsync(ct);
                try { await sub.Writer.WriteLineAsync(ev); }
                finally { writeLock.Release(); }
            }
        }
        catch { /* пайп разорван, отменено или writeLock уже снят — HandleClientAsync сам подчистит подписку */ }
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
                    // Ревью п.5: выключение переехало ВНУТРЬ SelfUpdater,
                    // к месту непосредственно перед запуском установщика —
                    // не сюда, перед скачиванием. Изначальный диагноз
                    // ("kill-switch блокирует саму службу") не подтвердился
                    // живым тестом (см. TunnelController.UpdateCoreAsync);
                    // настоящая причина была в DNS. Сам установщик всё
                    // равно перезапустит службу с нуля — включать туннель
                    // обратно после незачем, поэтому колбэк лишь выключает.
                    var appVersion = await SelfUpdater.CheckAndRunAsync(_log, _controller.OffAsync, ct);
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
        List<Subscriber> targets;
        lock (_subscribersLock) { targets = [.. _subscribers]; }
        foreach (var sub in targets)
        {
            // TryWrite никогда не блокируется — это и есть весь смысл
            // фикса (ревью п.8): вызывающий SetState держит единственный
            // на всю службу _gate, зависание здесь означало зависание
            // всей службы для всех клиентов.
            if (!sub.Queue.Writer.TryWrite(ev))
            {
                _log("IPC: подписчик не успевал вычитывать события — отключаю");
                try { sub.Pipe.Dispose(); } catch { /* могла закрыться сама */ }
            }
        }
    }

    private static async Task WriteAsync(StreamWriter writer, SemaphoreSlim writeLock, IpcResponse resp)
    {
        await writeLock.WaitAsync();
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(resp)); }
        finally { writeLock.Release(); }
    }
}
