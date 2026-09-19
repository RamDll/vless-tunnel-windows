using System.ServiceProcess;
using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using VlessTunnel.Native;
using VlessTunnel.Service;

// Использование:
//   VlessTunnel.Service.exe run <файл-со-ссылкой> <путь-к-xray.exe> <рабочая-директория> [--duration N] [--killswitch]
//   VlessTunnel.Service.exe serve <путь-к-xray.exe> <рабочая-директория> [--killswitch] [--duration N] [--allow-user ИМЯ]
//   VlessTunnel.Service.exe service [xray.exe] [рабочая-директория] [--no-killswitch] [--allow-user ИМЯ]
//   VlessTunnel.Service.exe doctor
//   VlessTunnel.Service.exe close-tray
//
// run — временный отладочный вход (план, этап 2), поднимает один туннель
// напрямую и ждёт Ctrl+C/"off"/--duration.
//
// serve (план, этап 4, 3.4/3.5) — то же самое, что делает "service" ниже,
// но консольным процессом, а не под SCM — для ручных прогонов на стенде.
// Принимает on/off/toggle/restart/status/set-link/doctor от VlessTunnel.Cli.
//
// service (план, 3.7 — то, что регистрирует установщик через
// `sc.exe create`) — тот же IpcServer/TunnelController, но под
// System.ServiceProcess.ServiceBase: SCM управляет стартом/остановкой,
// лог идёt в Event Log, а не в консоль (её и нет при запуске под SCM).
// Пути по умолчанию — рядом с исполняемым файлом (xray.exe) и
// %ProgramData%\vless-tunnel (план, раздел 2, таблица путей на машине).
//
// doctor (план, 3.3/3.9) — аварийный поиск и снятие зависших WFP-
// фильтров kill-switch'а, даже если они остались от процесса, который
// уже не запущен (например, после сбоя без штатного off).

if (args.Length >= 1 && args[0] == "service")
{
    var svcAllowUserIndex = Array.IndexOf(args, "--allow-user");
    var svcAllowUser = svcAllowUserIndex >= 0 && svcAllowUserIndex + 1 < args.Length ? args[svcAllowUserIndex + 1] : null;
    var svcKillSwitch = !args.Contains("--no-killswitch"); // kill-switch не опция (план, 3.3) — включён по умолчанию

    // Позиционные аргументы (xray.exe, рабочая папка) — то, что осталось
    // ПОСЛЕ вычитания служебных флагов и их значений, а не просто "всё,
    // что не начинается с --": --allow-user ИМЯ сам занимает позицию
    // сразу после "service" в реальном вызове установщика (binPath=
    // "...VlessTunnel.Service.exe" service --allow-user ИМЯ, без явных
    // xray.exe/рабочей папки) — голая проверка "не начинается с --"
    // ошибочно принимала бы ИМЯ пользователя за рабочую папку. Не
    // всплывало раньше, потому что во всех живых тестах этой сессии
    // xray.exe/рабочая папка передавались явно, ДО --allow-user.
    var positional = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--allow-user") { i++; continue; }
        if (args[i] == "--no-killswitch") continue;
        positional.Add(args[i]);
    }

    var programDir = AppContext.BaseDirectory;
    var svcXrayPath = positional.Count > 0 ? positional[0] : Path.Combine(programDir, "xray.exe");
    var svcWorkDir = positional.Count > 1
        ? positional[1]
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vless-tunnel");

    ServiceBase.Run(new VlessTunnelWindowsService(svcXrayPath, svcWorkDir, svcKillSwitch, svcAllowUser));
    return 0;
}

if (args.Length >= 1 && args[0] == "serve")
{
    var xrayPath = args.Length > 1 ? args[1] : throw new ArgumentException("нужен путь к xray.exe");
    var serveWorkDir = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : Directory.GetCurrentDirectory();
    Directory.CreateDirectory(serveWorkDir);
    var serveKillSwitch = args.Contains("--killswitch");
    var serveDurationIndex = Array.IndexOf(args, "--duration");
    var serveDuration = serveDurationIndex >= 0 && serveDurationIndex + 1 < args.Length ? int.Parse(args[serveDurationIndex + 1]) : (int?)null;
    var allowUserIndex = Array.IndexOf(args, "--allow-user");
    var allowUser = allowUserIndex >= 0 && allowUserIndex + 1 < args.Length ? args[allowUserIndex + 1] : null;

    void ServeLog(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Console.Out.Flush();
    }

    var controller = new TunnelController(xrayPath, Path.Combine(serveWorkDir, "config-service.json"), serveKillSwitch, ServeLog);
    var ipc = new IpcServer(controller, ServeLog, allowUser);

    using var serveCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; serveCts.Cancel(); };
    if (serveDuration is { } sd) serveCts.CancelAfter(TimeSpan.FromSeconds(sd));

    ServeLog($"IPC-сервер запущен на \\\\.\\pipe\\{VlessTunnel.Core.Ipc.IpcCommands.PipeName}");
    try { await ipc.RunAsync(serveCts.Token); }
    catch (OperationCanceledException) { }
    finally
    {
        ServeLog("Останавливаюсь, снимаю туннель если поднят...");
        await controller.OffAsync();
        ServeLog("Готово.");
    }
    return 0;
}

// close-tray (ревью п.23) — закрыть VlessTunnel.Tray.exe тем же способом,
// что self-update (SelfUpdater.CloseRunningTray): вызывается установщиком
// из [Code] ПЕРЕД sc stop/удалением файлов, иначе занятый файл трея не
// даёт снести {app} целиком, а иконка остаётся висеть с мёртвой службой.
if (args.Length >= 1 && args[0] == "close-tray")
{
    void CloseTrayLog(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    SelfUpdater.CloseRunningTray(CloseTrayLog);
    return 0;
}

if (args.Length >= 1 && args[0] == "doctor")
{
    void DoctorLog(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    try
    {
        var result = NetworkDoctor.Run(DoctorLog);
        DoctorLog($"Снято фильтров: {result.FiltersRemoved}, маршрутов: {result.RoutesRemoved}");
        await DoctorClockCheckAsync(DoctorLog);
        return 0;
    }
    catch (Exception ex)
    {
        DoctorLog($"doctor упал: {ex}");
        return 1;
    }
}

// Общий для standalone "doctor" и IPC-команды (VlessTunnel.Core.ClockCheck) —
// порог 5 минут: REALITY/TLS обычно допускают секунды-десятки секунд
// расхождения, но ломаются на минутах, поэтому предупреждаем с запасом,
// не на первой же секунде дрейфа часов.
static async Task DoctorClockCheckAsync(Action<string> log)
{
    var result = await VlessTunnel.Core.ClockCheck.CheckAsync();
    if (!result.Ok)
    {
        log($"Часы: не удалось проверить ({result.Error})");
        return;
    }
    var skew = result.SkewSeconds!.Value;
    if (Math.Abs(skew) < 300)
        log($"Часы: в порядке (расхождение {skew:F0} с)");
    else
        log($"Часы: РАСХОДЯТСЯ на {skew:F0} с с сетевым временем — REALITY/TLS может не работать, проверьте дату/время системы");
}

if (args.Length < 3 || args[0] != "run")
{
    Console.Error.WriteLine("Использование: run <файл-со-ссылкой> <путь-к-xray.exe> <рабочая-директория> [--duration N] [--killswitch]");
    Console.Error.WriteLine("           или: doctor");
    return 1;
}

var linkPath = args[1];
var xrayExePath = args[2];
var workDir = args.Length > 3 && args[3] != "--duration" ? args[3] : Directory.GetCurrentDirectory();
Directory.CreateDirectory(workDir);

var durationIndex = Array.IndexOf(args, "--duration");
var durationSeconds = durationIndex >= 0 && durationIndex + 1 < args.Length ? int.Parse(args[durationIndex + 1]) : (int?)null;
var killSwitch = args.Contains("--killswitch");

var link = LinkParser.Parse(await File.ReadAllTextAsync(linkPath));
var options = new WindowsConfigOptions { Outbound = new BuildOptions() };
var configPath = Path.Combine(workDir, "config-service.json");

void Log(string message)
{
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    Console.Out.Flush(); // на случай падения процесса (например, access violation в P/Invoke) — иначе буферизованный вывод через cmd.exe теряется
}

var manager = new TunnelManager(options, xrayExePath, configPath, Log, killSwitch);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    try
    {
        await manager.StartAsync(link, cts.Token);
    }
    catch (Exception ex)
    {
        Log($"Не удалось поднять туннель: {ex}");
        return 1;
    }

    if (durationSeconds is { } d)
    {
        Log($"Туннель поднят. Автоматически снимется через {d} с.");
        cts.CancelAfter(TimeSpan.FromSeconds(d));
    }
    else
    {
        Log("Туннель поднят. Ctrl+C или строка \"off\" — снять.");
    }

    _ = Task.Run(() =>
    {
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            if (line.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                cts.Cancel();
                return;
            }
        }
    });

    try { await Task.Delay(Timeout.Infinite, cts.Token); }
    catch (OperationCanceledException) { }
}
finally
{
    Log("Снимаю туннель...");
    await manager.StopAsync();
    Log("Готово.");
}

return 0;
