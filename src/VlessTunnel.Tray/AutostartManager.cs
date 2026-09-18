namespace VlessTunnel.Tray;

/// <summary>
/// Ярлык в Startup для пользователя (план, 3.6: "автозапуск трея"). Через
/// позднее связывание с WScript.Shell (COM), а не через сгенерированную во
/// время сборки COM-обёртку (`&lt;COMReference&gt;`) — сборка этого решения
/// идёт кросс-компиляцией на Linux-хосте (см. VlessTunnel.Native.csproj),
/// где COM-типов для tlbimp просто нет; позднее связывание работает
/// одинаково независимо от того, где собрано, и активируется только во
/// время выполнения — на самой Windows-машине, где COM есть.
/// </summary>
public static class AutostartManager
{
    private const string ShortcutName = "vless-tunnel.lnk";

    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    public static bool IsEnabled() => File.Exists(ShortcutPath);

    public static void Enable(string exePath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен (COM)");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try
            {
                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
                shortcut.IconLocation = exePath;
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static void Disable()
    {
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
    }
}
