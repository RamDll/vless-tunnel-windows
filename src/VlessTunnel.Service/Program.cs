using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using VlessTunnel.Native;
using VlessTunnel.Service;

// Временный отладочный вход (план, этап 2: "on/off через временный
// отладочный вход" — до полноценного IPC+CLI из этапа 4).
//
// Использование:
//   VlessTunnel.Service.exe run <файл-со-ссылкой> <путь-к-xray.exe> <рабочая-директория> [--duration N] [--killswitch]
//   VlessTunnel.Service.exe serve <путь-к-xray.exe> <рабочая-директория> [--killswitch] [--duration N] [--allow-user ИМЯ]
//   VlessTunnel.Service.exe doctor
//
// run — старый временный отладочный вход (план, этап 2), поднимает один
// туннель напрямую и ждёт Ctrl+C/"off"/--duration.
//
// serve (план, этап 4, 3.4/3.5) — сервер IPC-канала \\.\pipe\vless-tunnel:
// принимает on/off/toggle/restart/status/set-link/doctor от VlessTunnel.Cli.
// --duration тут тоже только для автопрогонов на стенде (сам процесс
// завершится через N секунд, предварительно сняв туннель, если он поднят).
//
// doctor (план, 3.3/3.9) — аварийный поиск и снятие зависших WFP-
// фильтров kill-switch'а, даже если они остались от процесса, который
// уже не запущен (например, после сбоя без штатного off).

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

if (args.Length >= 1 && args[0] == "doctor")
{
    void DoctorLog(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    try
    {
        var removed = KillSwitch.Doctor(DoctorLog);
        DoctorLog($"Снято фильтров: {removed}");
        return 0;
    }
    catch (Exception ex)
    {
        DoctorLog($"doctor упал: {ex}");
        return 1;
    }
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
