namespace VlessTunnel.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Без этого два запущенных трея (автозапуск при входе + повторный
        // ручной запуск, например после случайного двойного клика по
        // ярлыку) заводят два независимых значка и два независимых IPC-
        // подключения к службе — снаружи выглядит как непредсказуемые
        // зависания/ошибки при нажатии "Включить/выключить" (найдено на
        // реальной машине тестировщика: один процесс из автозапуска, один
        // запущен вручную поверх). Именованный мьютекс без "Global\" —
        // это не терминал-сервер, одна интерактивная сессия на пользователя
        // вполне достаточна.
        using var singleInstance = new Mutex(initiallyOwned: true, name: "VlessTunnelTraySingleInstance", createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "vless-tunnel уже запущен — ищите значок в системном трее (может быть свёрнут в стрелку \"^\" рядом с часами).",
                "vless-tunnel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());
    }
}
