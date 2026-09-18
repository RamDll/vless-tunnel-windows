using VlessTunnel.Core;
using VlessTunnel.Core.Ipc;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Service;

/// <summary>
/// Состояние туннеля поверх <see cref="TunnelManager"/> — то, чем реально
/// управляют IPC-команды (план, 3.4/3.5: on/off/toggle/restart/status/
/// set-link). Один экземпляр на процесс службы, все операции — под
/// одним замком, чтобы не поднять/не снять туннель дважды параллельно
/// из двух разных IPC-соединений.
/// </summary>
public sealed class TunnelController
{
    private readonly string _xrayExePath;
    private readonly string _configPath;
    private readonly bool _killSwitch;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TunnelManager? _manager;
    private ParsedLink? _link;
    private TunnelState _state = TunnelState.Off;
    private string? _error;

    // Версия Xray "зашита в сборку" (план, 3.7) — то же значение, что
    // в vm/pinned-versions.txt; отдельного запроса к xray.exe не делаем,
    // чтобы не платить процессом ради одной строки в окне.
    public const string XrayVersion = "26.3.27";

    public event Action<TunnelStatus>? StatusChanged;

    public TunnelController(string xrayExePath, string configPath, bool killSwitch, Action<string> log)
    {
        _xrayExePath = xrayExePath;
        _configPath = configPath;
        _killSwitch = killSwitch;
        _log = log;
    }

    public TunnelStatus GetStatus() => new()
    {
        State = _state,
        ServerHost = _link?.Host,
        ServerPort = _link?.Port,
        Network = _link?.Network,
        Security = _link?.Security,
        CoreVersion = XrayVersion,
        AutostartEnabled = false, // автозапуск — дело трея (AutostartManager, локальный ярлык), не службы
        Error = _error,
    };

    public void SetLink(string linkText)
    {
        // Разбираем ВНЕ замка _gate — set-link не трогает работающий туннель,
        // это только подготовка к следующему on (план, 3.5: set-link — своя команда, не часть on).
        _link = LinkParser.Parse(linkText);
        _log($"set-link: host={_link.Host}");
    }

    public async Task OnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state == TunnelState.On) return; // идемпотентно, не ошибка
            if (_link is null) throw new InvalidOperationException("Ссылка не задана (сначала set-link)");

            SetState(TunnelState.Starting);
            _manager = new TunnelManager(new WindowsConfigOptions { Outbound = new BuildOptions() }, _xrayExePath, _configPath, _log, _killSwitch);
            try
            {
                await _manager.StartAsync(_link, ct);
                _error = null;
                SetState(TunnelState.On);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                SetState(TunnelState.Error);
                await SafeStopAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OffAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_state == TunnelState.Off) return;
            SetState(TunnelState.Stopping);
            await SafeStopAsync();
            _error = null;
            SetState(TunnelState.Off);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ToggleAsync(CancellationToken ct)
    {
        if (_state == TunnelState.On) await OffAsync();
        else await OnAsync(ct);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await OffAsync();
        await OnAsync(ct);
    }

    private async Task SafeStopAsync()
    {
        if (_manager is null) return;
        try { await _manager.StopAsync(); }
        catch (Exception ex) { _log($"Остановка туннеля упала (не критично): {ex.Message}"); }
        _manager = null;
    }

    private void SetState(TunnelState state)
    {
        _state = state;
        StatusChanged?.Invoke(GetStatus());
    }
}
