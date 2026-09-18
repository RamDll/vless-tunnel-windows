using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using VlessTunnel.Core;

namespace VlessTunnel.Service;

/// <summary>
/// self-update (план, 3.9: "скачивание установщика, сверка sha256 И
/// отпечатка подписи с зашитым, запуск установщика тихо") — скачивает
/// последний релиз vless-tunnel-windows, проверяет ОБА условия перед
/// запуском (несовпадение любого — отказ, не предупреждение), запускает
/// установщик отдельным процессом и не ждёт его завершения: установщик
/// сам остановит эту же службу как часть обычного апгрейда (тот же путь,
/// что уже живьём проверен в этапе 6), а не ждёт разрешения от процесса,
/// который вот-вот остановят.
/// </summary>
public static class SelfUpdater
{
    // Отпечаток сертификата подписи (installer/trust.ps1 сверяет тот же) —
    // не секрет, это как раз то значение, с которым скачанное сверяется.
    public const string ExpectedCertThumbprint = "4E84442637C6083B440E6920B79DB36438544A1E";

    public static async Task<string> CheckAndRunAsync(Action<string> log, CancellationToken ct)
    {
        var release = await GitHubReleaseClient.GetLatestAsync("RamDll", "vless-tunnel-windows", ct: ct);
        var exeAsset = release.Assets.FirstOrDefault(a => a.Name == "vless-tunnel-setup.exe")
            ?? throw new InvalidOperationException("В релизе не найден vless-tunnel-setup.exe");
        var shaAsset = release.Assets.FirstOrDefault(a => a.Name == "vless-tunnel-setup.exe.sha256")
            ?? throw new InvalidOperationException("В релизе не найден vless-tunnel-setup.exe.sha256");

        var tempDir = Path.Combine(Path.GetTempPath(), "vless-tunnel-self-update-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        var exePath = Path.Combine(tempDir, "vless-tunnel-setup.exe");
        var shaPath = Path.Combine(tempDir, "vless-tunnel-setup.exe.sha256");

        await GitHubReleaseClient.DownloadToFileAsync(exeAsset.DownloadUrl, exePath, ct: ct);
        await GitHubReleaseClient.DownloadToFileAsync(shaAsset.DownloadUrl, shaPath, ct: ct);

        // Формат файла — "<sha256>  <имя файла>" (тот же, что пишет
        // build.yml: Get-FileHash ... | Out-File).
        var shaLine = (await File.ReadAllTextAsync(shaPath, ct)).Trim();
        var expectedSha = shaLine.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant()
            ?? throw new InvalidOperationException("Не удалось разобрать .sha256 файл релиза");
        var actualSha = await GitHubReleaseClient.Sha256HexAsync(exePath, ct);
        if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"sha256 установщика не совпал: ожидали {expectedSha}, получили {actualSha} — НЕ запускаю");
        log("self-update: контрольная сумма SHA-256 проверена");

        // X509Certificate.CreateFromSignedFile — устарел (SYSLIB0057) как
        // способ ЗАГРУЗКИ сертификатов из файла (.cer/.pfx), но именно для
        // извлечения Authenticode-подписи ИЗ ПОДПИСАННОГО exe у
        // X509CertificateLoader прямого замену нет — тот читает отдельные
        // файлы сертификатов, не встроенную подпись PE. Здесь только
        // извлекается отпечаток для сверки с зашитым значением, не
        // строится цепочка доверия — обоснованное исключение.
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exePath));
#pragma warning restore SYSLIB0057
        if (!string.Equals(cert.Thumbprint, ExpectedCertThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Отпечаток подписи установщика не совпал: {cert.Thumbprint} (ожидался {ExpectedCertThumbprint}) — НЕ запускаю");
        log($"self-update: подпись подтверждена (отпечаток {cert.Thumbprint})");

        CloseRunningTray(log);

        Process.Start(new ProcessStartInfo(exePath, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
        log($"self-update: установщик {release.TagName} запущен, настройки сохранятся (тихое обновление)");
        return release.TagName;
    }

    // Установщик под /VERYSILENT молча откатывает ВСЮ установку, если
    // RestartManager не смог закрыть трей (диалог Abort/Retry/Ignore
    // автоматически отвечает Abort в тихом режиме — установщик завершается
    // "успешно", ничего не поменяв, а служба никак об этом не узнаёт).
    // Трей почти всегда запущен в момент self-update (это его нормальное
    // состояние) — нашлось живым тестом: свежий инсталлятор с исправленным
    // автозапуском службы несколько раз подряд молча ничего не делал,
    // пока трей был открыт. Закрываем его тут сами, а не полагаемся на
    // RestartManager внутри установщика.
    private static void CloseRunningTray(Action<string> log)
    {
        foreach (var proc in Process.GetProcessesByName("VlessTunnel.Tray"))
        {
            try
            {
                proc.CloseMainWindow();
                if (!proc.WaitForExit(3000)) proc.Kill();
                log("self-update: трей закрыт перед обновлением");
            }
            catch (Exception ex)
            {
                log($"self-update: не удалось закрыть трей (pid={proc.Id}): {ex.Message}");
            }
        }
    }
}
