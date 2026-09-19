using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using VlessTunnel.Core;
using VlessTunnel.Native;

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
    // Отпечатки сертификатов подписи, которым доверяем (installer/trust.ps1
    // сверяет первый же) — не секрет, это как раз то значение, с которым
    // скачанное сверяется. Список, а не одна константа (ревью п.3) — при
    // переходе на нормальную подпись (SignPath, PLAN-windows.md) новый
    // отпечаток нужно ДОБАВИТЬ сюда одним релизом РАНЬШЕ фактической смены
    // подписи, иначе self-update у всех уже поставивших программу сломается
    // на следующем же релизе (self-update скачивает бинарник, подписанный
    // уже НОВЫМ сертификатом, но сверяет со СТАРЫМ списком из СВОЕЙ, ещё не
    // обновлённой копии).
    public static readonly string[] ExpectedCertThumbprints =
    [
        "4E84442637C6083B440E6920B79DB36438544A1E", // самоподписанный, installer/trust.ps1
    ];

    /// <param name="turnOffTunnelAsync">Вызывается ПЕРЕД запуском
    /// установщика, не перед скачиванием (ревью п.5 — как и update-core,
    /// изначальный диагноз "kill-switch блокирует саму службу" не
    /// подтвердился живым тестом, настоящей причиной была DNS-проблема,
    /// исправленная раньше в этой же сессии; выключать нужно только
    /// потому, что установщик остановит и заменит саму службу — не
    /// раньше). Именно колбэк, а не прямой вызов TunnelController отсюда:
    /// SelfUpdater — static-класс без своего состояния, а IpcServer уже
    /// держит единственный экземпляр TunnelController на процесс.</param>
    public static async Task<string> CheckAndRunAsync(Action<string> log, Func<Task> turnOffTunnelAsync, CancellationToken ct)
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

        // Ревью п.3: X509Certificate.CreateFromSignedFile ТОЛЬКО извлекает
        // сертификат из PE — не проверяет, что подпись действительна и
        // покрывает файл целиком. Подделать блоб с нужным отпечатком, но
        // битой/отсутствующей подписью, тривиально; единственной реальной
        // привязкой оставался sha256 из того же релиза — то есть якоря не
        // было вовсе. AuthenticodeVerifier.IsValidlySigned (WinVerifyTrust,
        // VlessTunnel.Native) — то же самое, что делает сам Windows перед
        // диалогом "издатель не может быть проверен". Проверяем ЭТО первым,
        // отпечаток сверяем только после успешной проверки подписи.
        if (!AuthenticodeVerifier.IsValidlySigned(exePath))
            throw new InvalidOperationException("Подпись установщика недействительна (WinVerifyTrust отклонил файл) — НЕ запускаю");

        // X509Certificate.CreateFromSignedFile — устарел (SYSLIB0057) как
        // способ ЗАГРУЗКИ сертификатов из файла (.cer/.pfx), но именно для
        // извлечения Authenticode-подписи ИЗ ПОДПИСАННОГО exe у
        // X509CertificateLoader прямого замену нет — тот читает отдельные
        // файлы сертификатов, не встроенную подпись PE. Подпись уже
        // подтверждена WinVerifyTrust выше — здесь только читаем отпечаток
        // для сверки со списком доверенных.
#pragma warning disable SYSLIB0057
        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(exePath));
#pragma warning restore SYSLIB0057
        if (!ExpectedCertThumbprints.Contains(cert.Thumbprint, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Отпечаток подписи установщика не в списке доверенных: {cert.Thumbprint} — НЕ запускаю");
        log($"self-update: подпись подтверждена WinVerifyTrust, отпечаток в списке доверенных ({cert.Thumbprint})");

        CloseRunningTray(log);
        await turnOffTunnelAsync();

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
    //
    // Ревью п.23: та же проблема (файл трея занят, {app} не удаляется
    // целиком, иконка висит с мёртвой службой) есть у обычного удаления
    // через мастер — public, чтобы Program.cs ("close-tray") звал ЭТОТ ЖЕ
    // метод из [Code] секции vless-tunnel.iss, а не заводил вторую,
    // отдельно эволюционирующую реализацию закрытия трея в Pascal.
    public static void CloseRunningTray(Action<string> log)
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
