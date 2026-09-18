namespace VlessTunnel.Tray;

/// <summary>
/// Вставить/сменить ссылку (аналог SetLinkDialog в Linux-GUI) — одно и то
/// же окно и для первой настройки, и для смены сервера позже, отличается
/// только заголовком/подписью кнопки.
/// </summary>
public sealed class SetLinkDialog : Form
{
    private readonly TextBox _input;
    private readonly Button _apply;

    public string? Link { get; private set; }

    public SetLinkDialog(bool firstTime)
    {
        Text = firstTime ? "Настроить туннель" : "Сменить сервер";
        Width = 520;
        Height = 300;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12), RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var hint = new Label
        {
            Text = "Вставьте vless://-ссылку — можно вместе с текстом, ссылка будет найдена сама.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 40,
        };
        layout.Controls.Add(hint, 0, 0);

        _input = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9),
            // UseSystemPasswordChar не действует при Multiline=true (задокументированное
            // ограничение WinForms) — живым тестом обнаружено, что поле молча
            // показывало ссылку в открытом виде, хотя подсказка утверждала обратное.
            // Linux-версия (gui/vless-tunnel-gui.py, SetLinkDialog) тоже не маскирует
            // это поле — просто убрали ложную подсказку вместо имитации маскировки.
        };
        layout.Controls.Add(_input, 0, 1);

        var buttonsRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        _apply = new Button { Text = firstTime ? "Установить" : "Применить", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel };
        buttonsRow.Controls.Add(_apply);
        buttonsRow.Controls.Add(cancel);
        layout.Controls.Add(buttonsRow, 0, 2);

        Controls.Add(layout);
        AcceptButton = _apply;
        CancelButton = cancel;

        _apply.Click += (_, e) =>
        {
            var text = _input.Text.Trim();
            if (text.Length == 0) { DialogResult = DialogResult.None; return; }
            Link = text;
        };
    }
}
