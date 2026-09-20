using System.Reflection;
using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Tray;

/// <summary>
/// Значок трея + меню (план, 3.6) — по образцу <c>gui/vless-tunnel-tray.py</c>
/// у Linux-версии: строка статуса сверху, включить/выключить, «Открыть
/// окно», автозапуск, подменю «Ещё» (сменить сервер/проверить/журнал/
/// диагностика/captive portal) — все реальные действия делегированы в
/// <see cref="MainWindow"/> (одна реализация на оба меню), выход.
/// Состояние обновляется подпиской на события IPC
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
        // Ревью (живой тест пользователя): раньше все три пункта ниже были
        // заглушками ("открыть окно" вместо реального действия) — забыли
        // дописать при переносе логики из MainWindow.BuildActionsMenu,
        // который эти же самые команды уже давно умеет выполнять по-настоящему.
        // Переиспользуем ЕГО методы (сделаны public), а не дублируем логику
        // тут — одна реализация на оба меню (трей и окно).
        more.DropDownItems.Add("Сменить сервер", null, async (_, _) => await _window.ShowSetLinkDialogAsync(firstTime: false));
        more.DropDownItems.Add("Проверить туннель", null, async (_, _) => await _window.RunTestAsync());
        more.DropDownItems.Add("Показать журнал", null, (_, _) => _window.ShowLog());
        more.DropDownItems.Add("Диагностика", null, async (_, _) => await _window.RunDoctorAsync());
        more.DropDownItems.Add(new ToolStripSeparator());
        // Captive portal (план, 3.9) — прямой пункт трея, не через окно:
        // именно в момент "застрял на гостиничном Wi-Fi без интернета"
        // открывать окно и идти в его меню — лишний шаг, когда цель ровно
        // противоположная (сделать что-то БЫСТРО, пока не пропало терпение).
        more.DropDownItems.Add("Пустить на 5 минут напрямую", null, async (_, _) => await CaptivePortalBypassAsync());
        menu.Items.Add(more);

        menu.Items.Add(new ToolStripSeparator());
        // Живой тест пользователя: "выход из трея не отключает текущий
        // туннель" — раньше ExitThread() звался напрямую, туннель (и
        // желаемое состояние "on", которое служба сама восстановит после
        // перезагрузки) оставался как есть. Явно гасим перед выходом —
        // best-effort, не блокируем закрытие трея, если служба не отвечает.
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconUnknown,
            Visible = true,
            Text = "vless-tunnel",
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
        // Живой тест пользователя: левый клик должен открывать то же меню,
        // что и правый (сейчас — только правый, встроенным поведением
        // NotifyIcon+ContextMenuStrip). Публичного API "показать меню
        // прямо сейчас" у NotifyIcon нет — зовём ТОТ ЖЕ приватный метод,
        // что и сам компонент вызывает внутри себя на правый клик, чтобы
        // получить идентичное поведение (позиционирование, фокус, закрытие
        // по клику вовне), а не переизобретать его через menu.Show(...).
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(_notifyIcon, null);
        };

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

    private async Task CaptivePortalBypassAsync()
    {
        var confirm = MessageBox.Show(
            "На 5 минут выключит туннель, чтобы можно было открыть страницу входа гостиничного/кафешного Wi-Fi " +
            "(частая ситуация: сервер VLESS ещё недостижим через портал, а kill-switch уже не пускает вообще ничего). " +
            "Через 5 минут туннель включится снова сам.\n\nПродолжить?",
            "Пустить на 5 минут напрямую?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.CaptivePortal }, TimeSpan.FromSeconds(10));
            _window.AppendLog(resp.Ok
                ? "Туннель выключен на 5 минут (captive portal) — включится снова сам."
                : $"Не удалось выключить туннель: {resp.Error}");
        }
        catch (Exception ex)
        {
            _window.AppendLog($"Не удалось выключить туннель: {ex.Message}");
        }
    }

    private async Task ExitAsync()
    {
        try
        {
            await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Off }, TimeSpan.FromSeconds(15));
        }
        catch { /* выходим в любом случае — служба могла быть уже недоступна */ }
        ExitThread();
    }

    private async Task ToggleAsync()
    {
        try
        {
            // 180с, не 90 — см. комментарий в VlessTunnel.Cli/Program.cs
            // (тот же таймаут, тот же живой баг с MaxXrayLaunchAttempts).
            await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Toggle }, TimeSpan.FromSeconds(180));
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
