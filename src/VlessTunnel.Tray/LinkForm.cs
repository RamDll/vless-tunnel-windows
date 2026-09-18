using VlessTunnel.Core.Ipc;

namespace VlessTunnel.Tray;

/// <summary>
/// Окно (план, 3.6): ссылка (поле скрывает UUID — маскирующий ввод),
/// статус, кнопка «Проверить», журнал. Собрано кодом, не .resx-дизайнером
/// (в этой среде нет визуального редактора Visual Studio) — простая
/// компоновка, не претендует на многое сверх функциональности из плана.
/// Закрытие окна (крестик) прячет его, а не завершает приложение —
/// выход только через пункт меню трея «Выход».
/// </summary>
public sealed class LinkForm : Form
{
    private readonly IpcClient _ipc = new();

    private readonly TextBox _linkInput = new() { PasswordChar = '•', Width = 420 };
    private readonly Button _applyButton = new() { Text = "Применить ссылку" };
    private readonly Label _stateLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
    private readonly Label _hostLabel = new() { AutoSize = true };
    private readonly Button _toggleButton = new() { Text = "Включить" };
    private readonly Button _testButton = new() { Text = "Проверить" };
    private readonly TextBox _logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 460, Height = 220 };

    public LinkForm()
    {
        Text = "vless-tunnel";
        Width = 500;
        Height = 460;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };

        var linkRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        linkRow.Controls.Add(new Label { Text = "Ссылка:", AutoSize = true, Padding = new Padding(0, 6, 8, 0) });
        linkRow.Controls.Add(_linkInput);
        linkRow.Controls.Add(_applyButton);

        var statusRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        statusRow.Controls.Add(_stateLabel);
        statusRow.Controls.Add(new Label { Text = "   ", AutoSize = true });
        statusRow.Controls.Add(_hostLabel);

        var buttonsRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttonsRow.Controls.Add(_toggleButton);
        buttonsRow.Controls.Add(_testButton);

        layout.Controls.Add(linkRow);
        layout.Controls.Add(statusRow);
        layout.Controls.Add(buttonsRow);
        layout.Controls.Add(new Label { Text = "Журнал:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(_logBox);

        Controls.Add(layout);

        _applyButton.Click += async (_, _) => await ApplyLinkAsync();
        _toggleButton.Click += async (_, _) => await ToggleAsync();
        _testButton.Click += async (_, _) => await TestAsync();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };

        _ = RefreshStatusAsync();
    }

    public void AppendLog(string line) => AppendLogLine(line);

    public void ApplyStatus(TunnelStatus status)
    {
        _stateLabel.Text = status.State switch
        {
            TunnelState.Off => "Выключено",
            TunnelState.Starting => "Включается…",
            TunnelState.On => "Включено",
            TunnelState.Stopping => "Выключается…",
            TunnelState.Error => $"Ошибка: {status.Error}",
            _ => status.State.ToString(),
        };
        _hostLabel.Text = status.ServerHost is { Length: > 0 } h ? $"Сервер: {h}" : "";
        _toggleButton.Text = status.State == TunnelState.On ? "Выключить" : "Включить";
        _toggleButton.Enabled = status.State is TunnelState.On or TunnelState.Off or TunnelState.Error;
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Status }, TimeSpan.FromSeconds(10));
            if (resp is { Ok: true, Status: { } s }) ApplyStatus(s);
        }
        catch (Exception ex)
        {
            AppendLogLine($"Не удалось получить статус: {ex.Message}");
        }
    }

    private async Task ApplyLinkAsync()
    {
        var link = _linkInput.Text.Trim();
        if (link.Length == 0) return;
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.SetLink, Link = link }, TimeSpan.FromSeconds(10));
            AppendLogLine(resp.Ok ? "Ссылка применена." : $"Ошибка: {resp.Error}");
            _linkInput.Clear();
        }
        catch (Exception ex)
        {
            AppendLogLine($"Не удалось применить ссылку: {ex.Message}");
        }
    }

    private async Task ToggleAsync()
    {
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = IpcCommands.Toggle }, TimeSpan.FromSeconds(90));
            if (resp is { Ok: true, Status: { } s }) ApplyStatus(s);
            else AppendLogLine($"Ошибка: {resp.Error}");
        }
        catch (Exception ex)
        {
            AppendLogLine($"Не удалось переключить туннель: {ex.Message}");
        }
    }

    private async Task TestAsync()
    {
        // "test" ещё не реализован на сервере (план, 3.5 — этап 7, живые
        // проверки HTTP/SOCKS5/UDP/DNS) — кнопка честно показывает это,
        // а не притворяется рабочей.
        try
        {
            var resp = await _ipc.SendAsync(new IpcRequest { Cmd = "test" }, TimeSpan.FromSeconds(10));
            AppendLogLine(resp.Ok ? "test: OK" : $"test: {resp.Error}");
        }
        catch (Exception ex)
        {
            AppendLogLine($"test: {ex.Message}");
        }
    }

    private void AppendLogLine(string line)
    {
        void Do() => _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        if (_logBox.InvokeRequired) _logBox.Invoke(Do);
        else Do();
    }
}
