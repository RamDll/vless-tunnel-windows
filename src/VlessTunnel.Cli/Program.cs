using System.IO.Pipes;
using System.Text.Json;
using VlessTunnel.Core.Ipc;

// vless-tunnel.exe — клиент IPC (план, 3.5). Команды, для которых уже
// есть серверная реализация (VlessTunnel.Service.exe serve, этап 4):
// on | off | toggle | restart | status [--json] | set-link [--stdin] | doctor.
//
// Ещё не реализовано на сервере (это отдельные, более поздние этапы —
// test/logs требуют собственно логов с ротацией и живых проверок
// HTTP/SOCKS5/UDP/DNS, autostart — планировщик задач, update-core —
// скачивание релиза с сверкой .dgst, ensure/uninstall — часть
// установщика): команда распознаётся, но явно сообщает, что не готова,
// вместо того чтобы притворяться рабочей.

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var cmd = args[0];

// watch — не часть команд из плана (3.5), отладочный инструмент для
// проверки подписки на события (план, 3.4: "трей не опрашивает службу
// по таймеру") без написания самого трея. Подключается, шлёт subscribe,
// печатает каждое пришедшее state-changed, пока не истечёт --seconds N.
if (cmd == "watch")
{
    var secondsIndex = Array.IndexOf(args, "--seconds");
    var seconds = secondsIndex >= 0 && secondsIndex + 1 < args.Length ? int.Parse(args[secondsIndex + 1]) : 30;

    using var watchPipe = new NamedPipeClientStream(".", IpcCommands.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await watchPipe.ConnectAsync(5000);
    using var watchReader = new StreamReader(watchPipe);
    await using var watchWriter = new StreamWriter(watchPipe) { AutoFlush = true };
    await watchWriter.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest { Cmd = IpcCommands.Subscribe }));

    using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
    try
    {
        string? evLine;
        while ((evLine = await watchReader.ReadLineAsync(watchCts.Token)) is not null)
            Console.WriteLine($"event: {evLine}");
    }
    catch (OperationCanceledException) { }
    return 0;
}

var notYetImplemented = new HashSet<string> { "logs", "autostart", "ensure", "uninstall" };
if (notYetImplemented.Contains(cmd))
{
    Console.Error.WriteLine($"\"{cmd}\" ещё не реализовано на сервере (этап 4 покрывает on/off/toggle/restart/status/set-link/doctor).");
    return 2;
}

// План, 3.5: "Таймауты клиента IPC должны быть больше худшего пути
// команды в службе" (урок Linux: toggle с таймаутом 30с при пути on >
// 31с). У нас худший путь on — bootstrap-резолв + запуск xray + ожидание
// адаптера (до 40с) + адрес/маршруты с повторами (до ~15с) + kill-switch.
var timeoutMs = cmd switch
{
    IpcCommands.On or IpcCommands.Off or IpcCommands.Toggle or IpcCommands.Restart => 90_000,
    IpcCommands.Doctor => 30_000,
    IpcCommands.Test => 40_000, // 4 проверки по ~6с таймаута каждая, последовательно
    IpcCommands.CaptivePortal => 10_000, // сама пауза — 5 минут, но снятие фильтров быстрое, ответ не ждёт истечения таймера
    IpcCommands.UpdateCore => 180_000, // скачивание ~35 МБ + возможный полный off/on с проверкой туннеля
    IpcCommands.SelfUpdate => 120_000, // скачивание установщика (~46 МБ) + проверки, без перезапуска туннеля
    _ => 10_000,
};

using var pipe = new NamedPipeClientStream(".", IpcCommands.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    await pipe.ConnectAsync(5000);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Не удалось подключиться к службе (\\\\.\\pipe\\{IpcCommands.PipeName}): {ex.Message}");
    return 3;
}

using var reader = new StreamReader(pipe);
await using var writer = new StreamWriter(pipe) { AutoFlush = true };

IpcRequest request = cmd switch
{
    IpcCommands.SetLink => new IpcRequest { Cmd = IpcCommands.SetLink, Link = await ReadLinkAsync(args) },
    _ => new IpcRequest { Cmd = cmd },
};

await writer.WriteLineAsync(JsonSerializer.Serialize(request));

using var cts = new CancellationTokenSource(timeoutMs);
string? line;
try
{
    line = await reader.ReadLineAsync(cts.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine($"Служба не ответила за {timeoutMs} мс.");
    return 4;
}

if (line is null)
{
    Console.Error.WriteLine("Служба закрыла соединение без ответа.");
    return 4;
}

var response = JsonSerializer.Deserialize<IpcResponse>(line);
if (response is null)
{
    Console.Error.WriteLine("Не удалось разобрать ответ службы.");
    return 4;
}

if (!response.Ok)
{
    Console.Error.WriteLine($"Ошибка: {response.Error}");
    return 5;
}

if (cmd == IpcCommands.Status)
{
    if (args.Contains("--json"))
    {
        Console.WriteLine(JsonSerializer.Serialize(response.Status));
    }
    else
    {
        Console.WriteLine($"Состояние: {response.Status?.State}");
        if (response.Status?.ServerHost is { } host) Console.WriteLine($"Сервер: {host}");
        if (response.Status?.Error is { } err) Console.WriteLine($"Ошибка: {err}");
    }
}
else if (cmd == IpcCommands.Doctor)
{
    Console.WriteLine($"Снято фильтров: {response.DoctorRemoved}");
    if (response.DoctorClockError is { } clockErr)
        Console.WriteLine($"Часы: не удалось проверить ({clockErr})");
    else if (response.DoctorClockSkewSeconds is { } skew)
        Console.WriteLine(Math.Abs(skew) < 300
            ? $"Часы: в порядке (расхождение {skew:F0} с)"
            : $"Часы: РАСХОДЯТСЯ на {skew:F0} с с сетевым временем — REALITY/TLS может не работать, проверьте дату/время системы");
}
else if (cmd == IpcCommands.CaptivePortal)
{
    Console.WriteLine("Туннель выключен на 5 минут — пройдите вход на странице портала Wi-Fi, потом туннель включится сам.");
}
else if (cmd == IpcCommands.UpdateCore)
{
    Console.WriteLine($"Ядро Xray обновлено до {response.UpdatedToVersion}.");
}
else if (cmd == IpcCommands.SelfUpdate)
{
    Console.WriteLine($"Скачан и проверен установщик {response.UpdatedToVersion}, запущен (настройки сохранятся).");
}
else if (cmd == IpcCommands.Test)
{
    var t = response.Test;
    if (t is null) { Console.WriteLine("Нет результатов."); return 6; }
    Console.WriteLine($"HTTP (127.0.0.1:10809):          {(t.Http ? "OK" : "FAIL")}");
    Console.WriteLine($"SOCKS5 (127.0.0.1:10808):        {(t.Socks5 ? "OK" : "FAIL")}");
    Console.WriteLine($"Прозрачный TCP (1.1.1.1:443):    {(t.TransparentTcp ? "OK" : "FAIL")}");
    Console.WriteLine($"DNS (example.com):                {(t.Dns ? "OK" : "FAIL")}");
    return t is { Http: true, Socks5: true, TransparentTcp: true, Dns: true } ? 0 : 7;
}
else
{
    Console.WriteLine($"OK, состояние: {response.Status?.State}");
}

return 0;

static async Task<string> ReadLinkAsync(string[] args)
{
    if (args.Contains("--stdin"))
        return (await Console.In.ReadToEndAsync()).Trim();
    if (args.Length > 1)
        return args[1];
    throw new ArgumentException("set-link: нужна ссылка аргументом или флаг --stdin");
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Использование: vless-tunnel <команда>
          on | off | toggle | restart
          status [--json]
          set-link <ссылка> | set-link --stdin
          doctor
          captive-portal
          update-core | self-update
        """);
}
