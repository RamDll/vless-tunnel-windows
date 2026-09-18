using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using VlessTunnel.Service;

// Временный отладочный вход (план, этап 2: "on/off через временный
// отладочный вход" — до полноценного IPC+CLI из этапа 4).
//
// Использование:
//   VlessTunnel.Service.exe run <файл-со-ссылкой> <путь-к-xray.exe> <рабочая-директория> [--duration N]
//
// Поднимает туннель и ждёт Ctrl+C, строки "off" на stdin, либо (если
// передан --duration) N секунд — затем аккуратно всё снимает и
// завершается. --duration нужен только для автоматических прогонов на
// стенде без интерактивного stdin (пайпы с задержкой через PowerShell на
// SSH ненадёжно передают вход именно в момент задержки, а не сразу же
// после её истечения) — в реальном сценарии on/off всегда будут приходить
// по IPC (этап 4), не через эту заглушку.

if (args.Length < 3 || args[0] != "run")
{
    Console.Error.WriteLine("Использование: run <файл-со-ссылкой> <путь-к-xray.exe> <рабочая-директория> [--duration N]");
    return 1;
}

var linkPath = args[1];
var xrayExePath = args[2];
var workDir = args.Length > 3 && args[3] != "--duration" ? args[3] : Directory.GetCurrentDirectory();
Directory.CreateDirectory(workDir);

var durationIndex = Array.IndexOf(args, "--duration");
var durationSeconds = durationIndex >= 0 && durationIndex + 1 < args.Length ? int.Parse(args[durationIndex + 1]) : (int?)null;

var link = LinkParser.Parse(await File.ReadAllTextAsync(linkPath));
var options = new WindowsConfigOptions { Outbound = new BuildOptions() };
var configPath = Path.Combine(workDir, "config-service.json");

void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

var manager = new TunnelManager(options, xrayExePath, configPath, Log);
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
