using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Tray;

/// <summary>
/// Значок трея + меню (план, 3.6): вкл/выкл/неизвестно, меню
/// включить-выключить/статус/вставить ссылку/логи/выход. Состояние
/// обновляется подпиской на события IPC (<see cref="IpcClient.SubscribeAsync"/>),
/// не опросом по таймеру — переподключается сама, если служба ещё не
/// запущена или временно недоступна (значок "неизвестно" в это время).
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly LinkForm _form = new();
    private readonly IpcClient _ipc = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Icon _iconOn;
    private readonly Icon _iconOff;
    private readonly Icon _iconUnknown;

    public TrayApplicationContext()
    {
        _ = _form.Handle; // форсируем создание хэндла заранее, чтобы Invoke из фонового потока подписки работал даже до первого показа окна

        _iconOn = LoadIcon("tray-on.ico");
        _iconOff = LoadIcon("tray-off.ico");
        _iconUnknown = LoadIcon("tray-unknown.ico");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Включить/выключить", null, async (_, _) => await ToggleAsync());
        menu.Items.Add("Статус", null, (_, _) => ShowWindow());
        menu.Items.Add("Вставить ссылку", null, (_, _) => ShowWindow());
        menu.Items.Add("Логи", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        var autostartItem = new ToolStripMenuItem("Автозапуск") { CheckOnClick = true, Checked = AutostartManager.IsEnabled() };
        autostartItem.Click += (_, _) => SetAutostart(autostartItem.Checked);
        menu.Items.Add(autostartItem);
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
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void SetAutostart(bool enabled)
    {
        try
        {
            if (enabled) AutostartManager.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath недоступен"));
            else AutostartManager.Disable();
            _form.AppendLog(enabled ? "Автозапуск включён." : "Автозапуск выключен.");
        }
        catch (Exception ex)
        {
            _form.AppendLog($"Не удалось изменить автозапуск: {ex.Message}");
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
            _form.AppendLog($"Не удалось переключить туннель: {ex.Message}");
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
                SetIcon(_iconUnknown, "vless-tunnel: служба недоступна");
                _form.AppendLog($"Соединение со службой потеряно: {ex.Message}");
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
        SetIcon(icon, $"vless-tunnel: {status.State}");
        RunOnFormThread(() =>
        {
            _form.ApplyStatus(status);
            _form.AppendLog($"Состояние: {status.State}");
        });
    }

    private void SetIcon(Icon icon, string text) => RunOnFormThread(() =>
    {
        _notifyIcon.Icon = icon;
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text; // ограничение NOTIFYICONDATA.szTip
    });

    private void RunOnFormThread(Action action)
    {
        if (_form.InvokeRequired) _form.Invoke(action);
        else action();
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _notifyIcon.Visible = false;
        _form.Dispose();
        base.ExitThreadCore();
    }
}
