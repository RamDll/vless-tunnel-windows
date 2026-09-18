namespace VlessTunnel.Tray;

/// <summary>Просмотр текста только для чтения — журнал/диагностика (аналог TextViewer в Linux-GUI).</summary>
public sealed class TextViewer : Form
{
    public TextViewer(string title, string body)
    {
        Text = title;
        Width = 640;
        Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = string.IsNullOrEmpty(body) ? "(пусто)" : body,
        };
        Controls.Add(box);
        Shown += (_, _) => { box.SelectionStart = box.TextLength; box.ScrollToCaret(); };
    }
}
