using System.Net;
using System.Net.Http;
using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Tray;

/// <summary>
/// Главное окно — аналог <c>gui/vless-tunnel-gui.py</c> (GTK4/libadwaita)
/// у Linux-версии, тот же набор возможностей средствами WinForms: карточка
/// статуса с переключателем и внешним IP, список параметров (сервер,
/// транспорт, ядро, прокси, автозапуск), меню действий (сменить сервер,
/// проверить, журнал, диагностика, удалить), экран первого запуска, пока
/// ссылка не задана.
/// </summary>
public sealed class MainWindow : Form
{
    private readonly IpcClient _ipc = new();
    private readonly List<string> _log = [];
    private readonly Lock _logLock = new();

    private readonly Panel _onboardingPanel;
    private readonly Panel _configuredPanel;
    private readonly Label _statusLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 13, FontStyle.Bold) };
    private readonly Label _ipLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _powerButton = new() { Width = 110, Height = 32 };
    private readonly Label _serverValue = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _transportValue = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _coreValue = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Label _proxyValue = new() { AutoSize = true, TextAlign = ContentAlignment.MiddleRight };
    private readonly Panel _proxyDot = new() { Width = 10, Height = 10 };
    private readonly CheckBox _autostartCheck = new() { Text = "", AutoSize = true };
    private readonly Button _menuButton = new() { Text = "☰", Width = 36, Height = 28 };

    private TunnelStatus? _lastStatus;
    private bool _autostartGuard;

    public MainWindow()
    {
        Text = "vless-tunnel";
        Width = 420;
        Height = 480;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "vless-tunnel.ico")); } catch { /* иконка не обязательна для работы окна */ }

        var header = new Panel { Dock = DockStyle.Top, Height = 40 };
        header.Controls.Add(new Label { Text = "vless-tunnel", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold), Location = new Point(10, 10) });
        _menuButton.Location = new Point(Width - 55, 6);
        _menuButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _menuButton.Click += (_, _) => BuildActionsMenu().Show(_menuButton, new Point(0, _menuButton.Height));
        header.Controls.Add(_menuButton);
        Controls.Add(header);

        _onboardingPanel = BuildOnboardingPanel();
        _configuredPanel = BuildConfiguredPanel();
        Controls.Add(_onboardingPanel);
        Controls.Add(_configuredPanel);

        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };

        _ = RefreshStatusAsync();
    }

    // ------------------------------------------------------------ onboarding
    private Panel BuildOnboardingPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false };
        var box = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Anchor = AnchorStyles.None,
        };
        box.Controls.Add(new Label { Text = "Туннель ещё не настроен", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) });
        box.Controls.Add(new Label
        {
            Text = "Вставьте вашу ссылку vless://, чтобы поднять туннель.",
            AutoSize = false,
            Width = 300,
            Height = 40,
            TextAlign = ContentAlignment.MiddleCenter,
        });
        var setupButton = new Button { Text = "Настроить", Width = 140, Height = 34, Margin = new Padding(0, 12, 0, 0) };
        setupButton.Click += async (_, _) => await ShowSetLinkDialogAsync(firstTime: true);
        box.Controls.Add(setupButton);

        panel.Controls.Add(box);
        panel.Resize += (_, _) => CenterInPanel(box, panel);
        return panel;
    }

    private static void CenterInPanel(Control child, Control parent) =>
        child.Location = new Point((parent.Width - child.Width) / 2, (parent.Height - child.Height) / 2);

    // ------------------------------------------------------------ configured
    private Panel BuildConfiguredPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false, Padding = new Padding(14, 50, 14, 14) };
        var stack = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true };

        // --- hero -----------------------------------------------------
        // Кнопка справа — в отдельной Dock=Right панели, а не через
        // Anchor.Right с фиксированным Location: последний внутри
        // TableLayoutPanel с AutoSize=true ширина hero на момент первой
        // раскладки была уже 0, якорь "приклеивался" за пределами видимой
        // области и кнопка пропадала целиком (найдено живым тестом).
        var hero = new Panel { Height = 70, Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 0, 0, 12) };
        var powerHost = new Panel { Width = 126, Dock = DockStyle.Right };
        _powerButton.Location = new Point(8, 18);
        _powerButton.Click += async (_, _) => await TogglePowerAsync();
        powerHost.Controls.Add(_powerButton);
        var textHost = new Panel { Dock = DockStyle.Fill };
        _statusLabel.Location = new Point(12, 10);
        _ipLabel.Location = new Point(12, 38);
        textHost.Controls.Add(_statusLabel);
        textHost.Controls.Add(_ipLabel);
        hero.Controls.Add(powerHost);
        hero.Controls.Add(textHost);
        stack.Controls.Add(hero);

        // --- info list --------------------------------------------------
        var list = new Panel { Dock = DockStyle.Top, BorderStyle = BorderStyle.FixedSingle, AutoSize = true };
        var rows = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddInfoRow(rows, "Сервер", _serverValue);
        AddInfoRow(rows, "Транспорт", _transportValue);
        AddInfoRow(rows, "Ядро", _coreValue);

        var proxyRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        proxyRow.Controls.Add(_proxyValue);
        proxyRow.Controls.Add(_proxyDot);
        AddInfoRow(rows, "Прокси", proxyRow);

        _autostartCheck.CheckedChanged += OnAutostartToggled;
        AddInfoRow(rows, "Автозапуск при входе", _autostartCheck);

        list.Controls.Add(rows);
        stack.Controls.Add(list);

        panel.Controls.Add(stack);
        return panel;
    }

    private static void AddInfoRow(TableLayoutPanel rows, string title, Control value)
    {
        var r = rows.RowCount;
        rows.RowCount = r + 1;
        rows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rows.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) }, 0, r);
        value.Anchor = AnchorStyles.Right;
        value.Margin = new Padding(0, 6, 0, 6);
        rows.Controls.Add(value, 1, r);
    }

    // -------------------------------------------------------------- menu
    private ContextMenuStrip BuildActionsMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Сменить сервер", null, async (_, _) => await ShowSetLinkDialogAsync(firstTime: false));
        menu.Items.Add("Проверить туннель", null, async (_, _) => await RunTestAsync());
        menu.Items.Add("Показать журнал", null, (_, _) => ShowLog());
        menu.Items.Add("Диагностика", null, async (_, _) => await RunDoctorAsync());
        menu.Items.Add("Пустить на 5 минут напрямую", null, async (_, _) => await RunCaptivePortalBypassAsync());
        menu.Items.Add(new ToolStripSeparator());
        var uninstall = new ToolStripMenuItem("Удалить") { ForeColor = Color.FromArgb(0xB2, 0x3A, 0x2E) };
        uninstall.Click += (_, _) => RunUninstaller();
        menu.Items.Add(uninstall);
        return menu;
    }

    // ------------------------------------------------------------- actions
    private async Task ShowSetLinkDialogAsync(bool firstTime)
    {
        using var dlg = new SetLinkDialog(firstTime);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Link is null) return;
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.SetLink, Link = dlg.Link }, TimeSpan.FromSeconds(10));
            AppendLog(resp.Ok ? "Ссылка применена." : $"Не удалось применить ссылку: {resp.Error}");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось применить ссылку: {ex.Message}");
        }
    }

    private async Task TogglePowerAsync()
    {
        _powerButton.Enabled = false;
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Toggle }, TimeSpan.FromSeconds(90));
            if (resp is { Ok: true, Status: { } s }) ApplyStatus(s);
            else AppendLog($"Не удалось переключить туннель: {resp.Error}");
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось переключить туннель: {ex.Message}");
        }
        finally
        {
            _powerButton.Enabled = true;
        }
    }

    private async Task RunTestAsync()
    {
        AppendLog("Проверяю туннель…");
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Test }, TimeSpan.FromSeconds(40));
            if (resp is { Ok: true, Test: { } t })
            {
                var report = $"HTTP:              {(t.Http ? "OK" : "FAIL")}\r\n" +
                              $"SOCKS5:            {(t.Socks5 ? "OK" : "FAIL")}\r\n" +
                              $"Прозрачный TCP:    {(t.TransparentTcp ? "OK" : "FAIL")}\r\n" +
                              $"DNS:               {(t.Dns ? "OK" : "FAIL")}";
                AppendLog(report);
                new TextViewer("Проверка туннеля", report).ShowDialog(this);
            }
            else
            {
                AppendLog($"Проверка не удалась: {resp.Error}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Проверка не удалась: {ex.Message}");
        }
    }

    private async Task RunDoctorAsync()
    {
        AppendLog("Собираю диагностику…");
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Doctor }, TimeSpan.FromSeconds(30));
            var report = resp.Ok ? $"Снято зависших WFP-фильтров: {resp.DoctorRemoved}" : $"Ошибка: {resp.Error}";
            AppendLog(report);
            new TextViewer("Диагностика", report).ShowDialog(this);
        }
        catch (Exception ex)
        {
            AppendLog($"Диагностика не удалась: {ex.Message}");
        }
    }

    private async Task RunCaptivePortalBypassAsync()
    {
        var confirm = MessageBox.Show(
            this,
            "На 5 минут выключит туннель, чтобы можно было открыть страницу входа гостиничного/кафешного Wi-Fi " +
            "(частая ситуация: сервер VLESS ещё недостижим через портал, а kill-switch уже не пускает вообще ничего). " +
            "Через 5 минут туннель включится снова сам.\n\nПродолжить?",
            "Пустить на 5 минут напрямую?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.CaptivePortal }, TimeSpan.FromSeconds(10));
            AppendLog(resp.Ok
                ? "Туннель выключен на 5 минут (captive portal) — включится снова сам."
                : $"Не удалось выключить туннель: {resp.Error}");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось выключить туннель: {ex.Message}");
        }
    }

    private void ShowLog()
    {
        string text;
        lock (_logLock) { text = string.Join(Environment.NewLine, _log); }
        new TextViewer("Журнал", text).ShowDialog(this);
    }

    private void RunUninstaller()
    {
        var confirm = MessageBox.Show(
            this,
            "Будут остановлены и удалены служба, маршруты и WFP-фильтры, конфиг и сам пакет vless-tunnel " +
            "целиком (программа, это окно, ярлыки). Действие необратимо.\n\nПродолжить?",
            "Удалить vless-tunnel полностью?",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes) return;

        // Деинсталлятор ставит сам установщик (Inno Setup — unins???.exe
        // рядом с программой); мы его только находим и запускаем — вся
        // логика остановки службы/снятия фильтров/вопроса про настройки
        // уже в нём (installer/vless-tunnel.iss, план 3.7/3.8).
        var uninstaller = Directory.GetFiles(AppContext.BaseDirectory, "unins*.exe").FirstOrDefault();
        if (uninstaller is null)
        {
            MessageBox.Show(this, "Деинсталлятор не найден рядом с программой (это не проблема при запуске не из установленной копии).", "Удалить", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        System.Diagnostics.Process.Start(uninstaller);
        Application.Exit();
    }

    private void OnAutostartToggled(object? sender, EventArgs e)
    {
        if (_autostartGuard) return;
        try
        {
            if (_autostartCheck.Checked) AutostartManager.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath недоступен"));
            else AutostartManager.Disable();
            AppendLog(_autostartCheck.Checked ? "Автозапуск включён." : "Автозапуск выключен.");
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось изменить автозапуск: {ex.Message}");
        }
    }

    // -------------------------------------------------------------- status
    private async Task RefreshStatusAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Status }, TimeSpan.FromSeconds(10));
            if (resp is { Ok: true, Status: { } s }) ApplyStatus(s);
        }
        catch (Exception ex)
        {
            AppendLog($"Не удалось получить статус: {ex.Message}");
        }
    }

    public void ApplyStatus(TunnelStatus status)
    {
        void Do()
        {
            _lastStatus = status;
            var configured = !string.IsNullOrEmpty(status.ServerHost);
            _onboardingPanel.Visible = !configured;
            _configuredPanel.Visible = configured;
            if (!configured) return;

            var active = status.State == TunnelState.On;
            _statusLabel.Text = status.State switch
            {
                TunnelState.Off => "Туннель выключен",
                TunnelState.Starting => "Включается…",
                TunnelState.On => "Туннель включён",
                TunnelState.Stopping => "Выключается…",
                TunnelState.Error => $"Ошибка: {status.Error}",
                _ => status.State.ToString(),
            };
            _powerButton.Text = active ? "Выключить" : "Включить";
            _powerButton.Enabled = status.State is TunnelState.On or TunnelState.Off or TunnelState.Error;

            _serverValue.Text = status.ServerPort is { } p ? $"{status.ServerHost}:{p}" : status.ServerHost;
            _transportValue.Text = string.Join(" · ", new[] { status.Network?.ToUpperInvariant(), status.Security }.Where(x => !string.IsNullOrEmpty(x)));
            _coreValue.Text = status.CoreVersion is { Length: > 0 } v ? $"Xray {v}" : "—";
            _proxyValue.Text = $"socks5 :{status.SocksPort}  http :{status.HttpPort}";
            _proxyDot.BackColor = active ? Color.FromArgb(0x2E, 0x7D, 0x4F) : Color.FromArgb(0xB2, 0x3A, 0x2E);

            _autostartGuard = true;
            _autostartCheck.Checked = AutostartManager.IsEnabled();
            _autostartGuard = false;

            _ = RefreshExternalIpAsync(active);
        }
        if (InvokeRequired) Invoke(Do); else Do();
    }

    private async Task RefreshExternalIpAsync(bool active)
    {
        _ipLabel.Text = "Внешний IP: проверяю…";
        try
        {
            using var handler = new HttpClientHandler();
            if (active) handler.Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{_lastStatus?.SocksPort ?? 10808}"));
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            var ip = await client.GetStringAsync("https://api.ipify.org");
            _ipLabel.Text = $"Внешний IP: {ip.Trim()}";
        }
        catch
        {
            _ipLabel.Text = "Внешний IP: не удалось проверить";
        }
    }

    public void AppendLog(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        lock (_logLock) { _log.Add(stamped); }
    }
}
