using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using VlessTunnel.Core;

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
    private RotatingFileLogger? _fileLogger;

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
        SecureConfigDirectory(_workDir);
        _cts = new CancellationTokenSource();
        _fileLogger = new RotatingFileLogger(Path.Combine(_workDir, "logs"));

        void Log(string message)
        {
            EventLog.WriteEntry(message, System.Diagnostics.EventLogEntryType.Information);
            try { _fileLogger.WriteLine(message); } catch { /* файловый лог — не критичный путь, Event Log уже записан */ }
        }

        _controller = new TunnelController(_xrayExePath, Path.Combine(_workDir, "config-service.json"), _killSwitch, Log);
        _ = _controller.RefreshCoreVersionAsync(); // ревью п.9 — реальная версия установленного xray.exe, не только после update-core
        _ = _controller.RestoreDesiredStateAsync(_cts.Token); // ревью п.10 — поднять туннель обратно (или снять осиротевшие WFP-фильтры), не оставлять как есть
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

    // Ревью п.2: %ProgramData% наследуемо даёт группе "Пользователи"
    // Read+Execute — link.txt (секретный UUID/pbk/sid VLESS-ссылки) и
    // config-service.json читал любой локальный пользователь, а не только
    // тот, кому явно разрешён IPC (--allow-user). На этом фоне редакция
    // UUID в логах (Redact.cs) была бессмысленна — секрет и так лежал
    // открытым файлом. Подтверждено живым тестом (icacls — Users:(RX)
    // до фикса). Снимаем унаследованные правила целиком (isProtected=true,
    // preserveInheritance=false) и оставляем только SYSTEM+Администраторы —
    // план (раздел 2) требует именно это. Вызывается на КАЖДОМ старте
    // службы, не только при первом создании каталога — чтобы каталоги от
    // установок ДО этого фикса тоже приводились к нужным правам, не только
    // свежесозданные.
    private static void SecureConfigDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        try { _controller?.OffAsync().GetAwaiter().GetResult(); }
        catch { /* при остановке службы — best effort, SCM всё равно ждёт ограниченное время */ }
        try { _runTask?.Wait(TimeSpan.FromSeconds(15)); }
        catch { /* не блокируем остановку службы дольше разумного */ }
        _fileLogger?.Dispose();
    }
}
