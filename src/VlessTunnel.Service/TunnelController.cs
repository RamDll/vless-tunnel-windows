using VlessTunnel.Core;
using VlessTunnel.Core.Ipc;
using VlessTunnel.Core.Models;

namespace VlessTunnel.Service;

/// <summary>
/// Состояние туннеля поверх <see cref="TunnelManager"/> — то, чем реально
/// управляют IPC-команды (план, 3.4/3.5: on/off/toggle/restart/status/
/// set-link). Один экземпляр на процесс службы, все операции — под
/// одним замком, чтобы не поднять/не снять туннель дважды параллельно
/// из двух разных IPC-соединений.
/// </summary>
public sealed class TunnelController
{
    private readonly string _xrayExePath;
    private readonly string _configPath;
    private readonly string _linkFilePath;
    private readonly bool _killSwitch;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TunnelManager? _manager;
    private ParsedLink? _link;
    private TunnelState _state = TunnelState.Off;
    private string? _error;

    // Версия Xray "зашита в сборку" (план, 3.7) — то же значение, что
    // в vm/pinned-versions.txt; отдельного запроса к xray.exe не делаем,
    // чтобы не платить процессом ради одной строки в окне.
    public const string XrayVersion = "26.3.27";

    public event Action<TunnelStatus>? StatusChanged;

    public TunnelController(string xrayExePath, string configPath, bool killSwitch, Action<string> log)
    {
        _xrayExePath = xrayExePath;
        _configPath = configPath;
        _killSwitch = killSwitch;
        _log = log;

        // Ссылка переживает перезапуск службы (обновление установщиком,
        // перезагрузку, ручной restart сервиса) — как в Linux-версии
        // (ETC_DIR/config.json). Найдено живым тестом установщика: без
        // этого "обновление поверх (настройки на месте)" из критериев
        // приёмки этапа 6 не выполнялось — set-link держался только в
        // памяти процесса и терялся при каждом рестарте службы.
        _linkFilePath = Path.Combine(Path.GetDirectoryName(_configPath) ?? ".", "link.txt");
        TryLoadPersistedLink();
    }

    private void TryLoadPersistedLink()
    {
        if (!File.Exists(_linkFilePath)) return;
        try
        {
            var saved = File.ReadAllText(_linkFilePath).Trim();
            if (saved.Length == 0) return;
            _link = LinkParser.Parse(saved);
            _log($"set-link (восстановлено): host={_link.Host}");
        }
        catch (Exception ex)
        {
            _log($"не удалось загрузить сохранённую ссылку из {_linkFilePath}: {ex.Message}");
        }
    }

    public TunnelStatus GetStatus() => new()
    {
        State = _state,
        ServerHost = _link?.Host,
        ServerPort = _link?.Port,
        Network = _link?.Network,
        Security = _link?.Security,
        CoreVersion = XrayVersion,
        AutostartEnabled = false, // автозапуск — дело трея (AutostartManager, локальный ярлык), не службы
        Error = _error,
    };

    public void SetLink(string linkText)
    {
        // Разбираем ВНЕ замка _gate — set-link не трогает работающий туннель,
        // это только подготовка к следующему on (план, 3.5: set-link — своя команда, не часть on).
        _link = LinkParser.Parse(linkText);
        _log($"set-link: host={_link.Host}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_linkFilePath) ?? ".");
            File.WriteAllText(_linkFilePath, linkText.Trim());
        }
        catch (Exception ex)
        {
            // Не фатально для текущего сеанса (ссылка уже применена в памяти) —
            // но переживёт перезапуск только если запись всё же получится.
            _log($"не удалось сохранить ссылку в {_linkFilePath}: {ex.Message}");
        }
    }

    public async Task OnAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state == TunnelState.On) return; // идемпотентно, не ошибка
            if (_link is null) throw new InvalidOperationException("Ссылка не задана (сначала set-link)");

            SetState(TunnelState.Starting);
            _manager = new TunnelManager(new WindowsConfigOptions { Outbound = new BuildOptions() }, _xrayExePath, _configPath, _log, _killSwitch);
            try
            {
                await _manager.StartAsync(_link, ct);
                _error = null;
                SetState(TunnelState.On);
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                SetState(TunnelState.Error);
                await SafeStopAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OffAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_state == TunnelState.Off) return;
            SetState(TunnelState.Stopping);
            await SafeStopAsync();
            _error = null;
            SetState(TunnelState.Off);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ToggleAsync(CancellationToken ct)
    {
        if (_state == TunnelState.On) await OffAsync();
        else await OnAsync(ct);
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        await OffAsync();
        await OnAsync(ct);
    }

    // Отложенный автовозврат после паузы captive portal — отдельное поле,
    // не часть _gate: пользователь может нажать "ещё 5 минут" повторно,
    // не дожидаясь предыдущего таймера.
    private CancellationTokenSource? _captivePortalCts;

    /// <summary>
    /// Captive portal (план, 3.9) — «Пустить на 5 минут напрямую». Снять
    /// только WFP-фильтры кило-switch'а НЕДОСТАТОЧНО: пока подняты
    /// /1-маршруты через TUN, весь трафик всё равно роутится в адаптер, а
    /// не напрямую через физический (xray без рабочего апстрима его
    /// молча дропает) — снаружи это выглядит так же безнадёжно, как и с
    /// фильтрами. Поэтому честный способ "дать пройти напрямую" — полностью
    /// снять туннель (маршруты+TUN+kill-switch вместе, той же проверенной
    /// машиной, что и обычный off) и автоматически поднять его обратно
    /// через <paramref name="duration"/>, если он был включён к моменту
    /// вызова. Сам туннель этим вызовом не включается — только
    /// восстанавливается то, что уже было.
    /// </summary>
    public async Task CaptivePortalBypassAsync(TimeSpan duration)
    {
        _captivePortalCts?.Cancel();
        var cts = new CancellationTokenSource();
        _captivePortalCts = cts;

        var hadManager = _manager is not null;
        await OffAsync();
        _log(hadManager
            ? $"Captive portal: туннель выключен на {duration.TotalMinutes:F0} мин для прямого доступа, потом включится снова"
            : "Captive portal: туннель и так был выключен, снимать было нечего");

        if (!hadManager) return; // не было чем блокировать — нечего и восстанавливать

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(duration, cts.Token); }
            catch (OperationCanceledException) { return; }
            try { await OnAsync(CancellationToken.None); }
            catch (Exception ex) { _log($"Captive portal: не удалось снова включить туннель после паузы: {ex.Message}"); }
        });
    }

    /// <summary>
    /// update-core (план, 3.9/3.5) — скачивает последний релиз Xray-core,
    /// сверяет sha256 с опубликованным .dgst (тот же формат, что уже
    /// разбирает Linux-версия), подменяет xray.exe/wintun.dll и
    /// перепроверяет туннель. Если туннель был включён и с новым ядром не
    /// поднимается или не проходит <see cref="TunnelTester"/> — откатывает
    /// файлы на резервную копию и поднимает обратно старое ядро, чтобы
    /// неудачное обновление не оставило пользователя без интернета.
    ///
    /// Туннель выключается ПЕРЕД подменой файлов, не перед скачиванием —
    /// пересмотрено (ревью п.5): изначальный диагноз ("пока kill-switch
    /// активен, самому процессу службы выйти на github.com тоже нельзя")
    /// не подтвердился живым тестом — permit-фильтр kill-switch'а на
    /// TUN-интерфейсе не различает процессы, скачивание от имени SYSTEM
    /// с включённым туннелем и активным kill-switch'ом прошло успешно
    /// (HTTP 200 на api.github.com). Настоящая причина исходной ошибки
    /// "Хост не обнаружен" — DNS-баг (TUN-адаптеру не назначался DNS-
    /// сервер), исправленный раньше в этой же сессии (TunnelManager.
    /// SetTunDns) и по чистой случайности совпавший по времени с тем
    /// самым "выключать перед скачиванием", из-за чего и решили, что
    /// дело в kill-switch. Это важно не ради красоты: если у
    /// пользователя весь остальной интернет (кроме GitHub) идёт только
    /// через сам туннель (цензурируемая сеть) — старое поведение оставляло
    /// его вовсе без интернета на всё время скачивания, а если у него
    /// сети без туннеля просто нет — access github.com только через
    /// VPN — старое поведение делало update-core невозможным в принципе.
    /// Файлы (xray.exe — это EXE-образ РАБОТАЮЩЕГО процесса) всё ещё
    /// нельзя подменить, пока процесс жив — поэтому выключение осталось,
    /// просто переехало к месту, где оно физически необходимо.
    /// </summary>
    public async Task<string> UpdateCoreAsync(CancellationToken ct)
    {
        var wasOn = _state == TunnelState.On;

        var release = await GitHubReleaseClient.GetLatestAsync("XTLS", "Xray-core", ct: ct);
        var asset = release.Assets.FirstOrDefault(a => a.Name == "Xray-windows-64.zip")
            ?? throw new InvalidOperationException("В релизе Xray-core не найден Xray-windows-64.zip");

        var tempDir = Path.Combine(Path.GetTempPath(), "vless-tunnel-update-core-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, asset.Name);
            await GitHubReleaseClient.DownloadToFileAsync(asset.DownloadUrl, zipPath, ct: ct);

            // Ревью п.4: раньше отсутствие/неразбираемость .dgst писало
            // предупреждение в лог и ВСЁ РАВНО подменяло xray.exe/wintun.dll
            // без единой проверки — единственная защита от подмены архива
            // (например, компрометации релиза апстрима или MITM без TLS-
            // пиннинга) была необязательной. Теперь любой из двух случаев —
            // жёсткий отказ ДО распаковки и подмены файлов, не предупреждение.
            var dgstAsset = release.Assets.FirstOrDefault(a => a.Name == asset.Name + ".dgst")
                ?? throw new InvalidOperationException($"В релизе Xray-core {release.TagName} нет {asset.Name}.dgst — контрольную сумму проверить нечем, НЕ подменяю файлы");
            var dgstPath = Path.Combine(tempDir, dgstAsset.Name);
            await GitHubReleaseClient.DownloadToFileAsync(dgstAsset.DownloadUrl, dgstPath, ct: ct);
            var expectedSha = GitHubReleaseClient.ParseSha256FromDgst(await File.ReadAllTextAsync(dgstPath, ct))
                ?? throw new InvalidOperationException($"Не удалось разобрать {dgstAsset.Name} — контрольную сумму проверить нечем, НЕ подменяю файлы");
            var actualSha = await GitHubReleaseClient.Sha256HexAsync(zipPath, ct);
            if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"sha256 архива Xray не совпал: ожидали {expectedSha}, получили {actualSha}");
            _log("update-core: контрольная сумма SHA-256 проверена");

            var extractDir = Path.Combine(tempDir, "extracted");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);
            var newXray = Path.Combine(extractDir, "xray.exe");
            var newWintun = Path.Combine(extractDir, "wintun.dll");
            if (!File.Exists(newXray))
                throw new InvalidOperationException("В архиве Xray-windows-64.zip нет xray.exe");

            var xrayDir = Path.GetDirectoryName(_xrayExePath) ?? ".";
            var wintunPath = Path.Combine(xrayDir, "wintun.dll");
            var backupXray = _xrayExePath + ".bak";
            var backupWintun = wintunPath + ".bak";

            // Только теперь, не раньше — xray.exe нельзя перезаписать, пока
            // его процесс жив (см. комментарий к методу), а до этого места
            // файл никак не трогали.
            if (wasOn) await OffAsync();

            File.Copy(_xrayExePath, backupXray, overwrite: true);
            if (File.Exists(wintunPath)) File.Copy(wintunPath, backupWintun, overwrite: true);
            File.Copy(newXray, _xrayExePath, overwrite: true);
            if (File.Exists(newWintun)) File.Copy(newWintun, wintunPath, overwrite: true);
            _log($"update-core: файлы заменены на {release.TagName}");

            if (wasOn)
            {
                try
                {
                    await OnAsync(ct);
                    var results = await TunnelTester.RunAsync();
                    if (results is not { Http: true, Socks5: true })
                        throw new InvalidOperationException("новое ядро поднялось, но туннель не проходит проверку (HTTP/SOCKS5)");
                    _log("update-core: новое ядро проверено, туннель работает");
                }
                catch (Exception ex)
                {
                    _log($"update-core: новое ядро не заработало ({ex.Message}) — откатываю прежнее");
                    await OffAsync();
                    File.Copy(backupXray, _xrayExePath, overwrite: true);
                    if (File.Exists(backupWintun)) File.Copy(backupWintun, wintunPath, overwrite: true);
                    await OnAsync(CancellationToken.None);
                    throw new InvalidOperationException($"новое ядро Xray не заработало, откачено на прежнее: {ex.Message}");
                }
            }

            File.Delete(backupXray);
            if (File.Exists(backupWintun)) File.Delete(backupWintun);
            return release.TagName;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* временная папка, не критично */ }
        }
    }

    private async Task SafeStopAsync()
    {
        if (_manager is null) return;
        try { await _manager.StopAsync(); }
        catch (Exception ex) { _log($"Остановка туннеля упала (не критично): {ex.Message}"); }
        _manager = null;
    }

    private void SetState(TunnelState state)
    {
        _state = state;
        StatusChanged?.Invoke(GetStatus());
    }
}
