using System.ServiceProcess;

namespace VlessTunnel.Service;

/// <summary>
/// Хост настоящей службы Windows (план, 3.7: установщик "регистрирует
/// службу") — тонкая обёртка над той же связкой TunnelController+IpcServer,
/// что и отладочная подкоманда <c>serve</c>. OnStart не блокирует (SCM
/// ждёт от него быстрого возврата), IPC-сервер крутится в фоновой задаче;
/// OnStop просит её остановиться и снимает туннель, если он был поднят.
/// </summary>
public sealed class VlessTunnelWindowsService : ServiceBase
{
    public const string WindowsServiceName = "vless-tunnel";

    private readonly string _xrayExePath;
    private readonly string _workDir;
    private readonly bool _killSwitch;
    private readonly string? _allowUser;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TunnelController? _controller;

    public VlessTunnelWindowsService(string xrayExePath, string workDir, bool killSwitch, string? allowUser)
    {
        ServiceName = WindowsServiceName;
        _xrayExePath = xrayExePath;
        _workDir = workDir;
        _killSwitch = killSwitch;
        _allowUser = allowUser;
    }

    protected override void OnStart(string[] args)
    {
        Directory.CreateDirectory(_workDir);
        _cts = new CancellationTokenSource();

        void Log(string message) => EventLog.WriteEntry(message, System.Diagnostics.EventLogEntryType.Information);

        _controller = new TunnelController(_xrayExePath, Path.Combine(_workDir, "config-service.json"), _killSwitch, Log);
        var ipc = new IpcServer(_controller, Log, _allowUser);

        // OnStart обязан вернуться быстро (SCM ждёт ~30с по умолчанию) —
        // сам цикл приёма IPC-соединений уходит в фоновую задачу.
        _runTask = Task.Run(async () =>
        {
            try { await ipc.RunAsync(_cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"IpcServer упал: {ex}"); }
        });
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try { _controller?.OffAsync().GetAwaiter().GetResult(); }
        catch { /* при остановке службы — best effort, SCM всё равно ждёт ограниченное время */ }
        try { _runTask?.Wait(TimeSpan.FromSeconds(15)); }
        catch { /* не блокируем остановку службы дольше разумного */ }
    }
}
