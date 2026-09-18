using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Tray;

/// <summary>
/// Значок трея + меню (план, 3.6) — по образцу <c>gui/vless-tunnel-tray.py</c>
/// у Linux-версии: строка статуса сверху, включить/выключить, «Открыть
/// окно», автозапуск, подменю «Ещё» (проверить/журнал/диагностика),
/// выход. Состояние обновляется подпиской на события IPC
/// (<see cref="IpcClient.SubscribeAsync"/>), не опросом по таймеру —
/// переподключается сама, если служба ещё не запущена или временно
/// недоступна (значок "неизвестно" в это время).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainWindow _window = new();
    private readonly IpcClient _ipc = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly Icon _iconUnknown;
    private readonly ToolStripMenuItem _statusItem = new("Проверяю…") { Enabled = false };
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApplicationContext()
    {
        _ = _window.Handle; // форсируем создание хэндла заранее, чтобы Invoke из фонового потока подписки работал даже до первого показа окна

        _iconOn = LoadIcon("tray-on.ico");
        _iconOff = LoadIcon("tray-off.ico");
        _iconUnknown = LoadIcon("tray-unknown.ico");

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Включить/выключить", null, async (_, _) => await ToggleAsync());
        menu.Items.Add("Открыть окно", null, (_, _) => ShowWindow());
        _autostartItem = new ToolStripMenuItem("Автозапуск при загрузке") { CheckOnClick = true, Checked = AutostartManager.IsEnabled() };
        _autostartItem.Click += (_, _) => SetAutostart(_autostartItem.Checked);
        menu.Items.Add(_autostartItem);

        var more = new ToolStripMenuItem("Ещё");
        more.DropDownItems.Add("Проверить туннель", null, (_, _) => ShowWindow());
        more.DropDownItems.Add("Показать журнал", null, (_, _) => ShowWindow());
        more.DropDownItems.Add("Диагностика", null, (_, _) => ShowWindow());
        menu.Items.Add(more);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconUnknown,
            Visible = true,
            Text = "vless-tunnel",
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        _ = SubscribeLoopAsync(_cts.Token);
    }

    private static Icon LoadIcon(string fileName) =>
        new(Path.Combine(AppContext.BaseDirectory, fileName));

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            if (enabled) AutostartManager.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath недоступен"));
            else AutostartManager.Disable();
            _window.AppendLog(enabled ? "Автозапуск включён." : "Автозапуск выключен.");
        }
        catch (Exception ex)
        {
            _window.AppendLog($"Не удалось изменить автозапуск: {ex.Message}");
        }
    }

    private async Task ToggleAsync()
    {
        try
        {
            await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Toggle }, TimeSpan.FromSeconds(90));
        }
        catch (Exception ex)
        {
            _window.AppendLog($"Не удалось переключить туннель: {ex.Message}");
        }
    }

    private async Task SubscribeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _ipc.SubscribeAsync(OnStatus, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetIcon(_iconUnknown, "vless-tunnel: служба недоступна", "Служба недоступна");
                _window.AppendLog($"Соединение со службой потеряно: {ex.Message}");
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnStatus(TunnelStatus status)
    {
        var icon = status.State switch
        {
            TunnelState.On => _iconOn,
            TunnelState.Off => _iconOff,
            _ => _iconUnknown,
        };
        var statusText = status.State switch
        {
            TunnelState.Off => "Выключено",
            TunnelState.Starting => "Включается…",
            TunnelState.On => "Включено",
            TunnelState.Stopping => "Выключается…",
            TunnelState.Error => "Ошибка",
            _ => status.State.ToString(),
        };
        SetIcon(icon, $"vless-tunnel: {status.State}", statusText);
        RunOnFormThread(() =>
        {
            _window.ApplyStatus(status);
            _window.AppendLog($"Состояние: {status.State}");
        });
    }

    private void SetIcon(Icon icon, string tooltip, string statusText) => RunOnFormThread(() =>
    {
        _notifyIcon.Icon = icon;
        _notifyIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip; // ограничение NOTIFYICONDATA.szTip
        _statusItem.Text = statusText;
    });

    private void RunOnFormThread(Action action)
    {
        if (_window.InvokeRequired) _window.Invoke(action);
        else action();
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _notifyIcon.Visible = false;
        _window.Dispose();
        base.ExitThreadCore();
    }
}
