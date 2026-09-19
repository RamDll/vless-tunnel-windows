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

    // Ревью п.9: версия "зашитая в сборку" (план, 3.7, то же значение,
    // что в vm/pinned-versions.txt) была КОНСТАНТОЙ — после update-core
    // статус и окно продолжали показывать старую версию ядра, хотя файл
    // на диске уже другой. Теперь это дефолт/фолбэк для случая "ещё ни
    // разу не спрашивали" — реальная версия читается из самого xray.exe
    // (RefreshCoreVersionAsync), но КЭШИРУЕТСЯ в поле, не запрашивается
    // процессом на каждый status — дорого, а версия и не может измениться
    // сама по себе между обновлениями ядра.
    public const string XrayVersion = "26.3.27";
    private string _coreVersion = XrayVersion;

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
        _desiredStateFilePath = Path.Combine(Path.GetDirectoryName(_configPath) ?? ".", "desired-state.txt");
        TryLoadPersistedLink();
    }

    // Ревью п.10: OnStart поднимал только IPC — туннель ВСЕГДА стартовал
    // в Off, желаемое состояние нигде не хранилось. Постоянные
    // (persistent) WFP-фильтры при этом переживают и падение процесса, и
    // перезагрузку (см. doc-комментарий KillSwitch) — то есть после
    // reboot с включённым туннелем машина грузилась БЕЗ интернета
    // (фильтры на месте, TUN/xray — нет) до ручного включения; а
    // `sc failure` (этап 3.9) перезапускал упавшую службу, но туннель
    // не поднимал — сам механизм автовосстановления не достигал цели.
    private readonly string _desiredStateFilePath;

    private void SaveDesiredState(bool on)
    {
        try { File.WriteAllText(_desiredStateFilePath, on ? "on" : "off"); }
        catch (Exception ex) { _log($"Не удалось сохранить желаемое состояние туннеля: {ex.Message}"); }
    }

    /// <summary>Вызывается один раз при старте службы (после конструктора,
    /// см. VlessTunnelWindowsService.OnStart) — не из конструктора: сама
    /// попытка поднять туннель асинхронна и может занять секунды, а
    /// конструктор обязан быть мгновенным.</summary>
    public async Task RestoreDesiredStateAsync(CancellationToken ct)
    {
        var desiredOn = File.Exists(_desiredStateFilePath) && File.ReadAllText(_desiredStateFilePath).Trim() == "on";
        if (desiredOn && _link is not null)
        {
            _log("Желаемое состояние — туннель включён, поднимаю после старта службы");
            try { await OnAsync(ct); }
            catch (Exception ex) { _log($"Не удалось восстановить туннель после старта службы: {ex.Message}"); }
            return;
        }

        // Желаемое состояние — выключено (или ещё не решено, первый
        // запуск) — но постоянные WFP-фильтры переживают падение
        // процесса/перезагрузку. Если служба стартовала НЕ подняв
        // туннель (наш собственный процесс), а фильтры уже есть —
        // это осиротевшие фильтры от предыдущего сеанса, снимаем сами,
        // не дожидаясь, пока пользователь заметит "нет интернета" и
        // сам полезет за doctor.
        try
        {
            var result = Native.NetworkDoctor.Run(s => _log($"doctor (старт службы): {s}"));
            if (result.FiltersRemoved > 0 || result.RoutesRemoved > 0)
                _log($"Старт службы: снято {result.FiltersRemoved} осиротевших WFP-фильтров и {result.RoutesRemoved} маршрутов (желаемое состояние — выключено)");
        }
        catch (Exception ex)
        {
            _log($"doctor при старте службы упал: {ex.Message}");
        }
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
        CoreVersion = _coreVersion,
        AutostartEnabled = false, // автозапуск — дело трея (AutostartManager, локальный ярлык), не службы
        Error = _error,
    };

    // Вызывается один раз при первом старте службы (Program.cs) и после
    // каждого успешного update-core — не на каждый status. "xray.exe
    // version" печатает первую строку вида "Xray 26.3.27 (Xray,
    // Penetrates Everything.) ...", версия — второе слово.
    public async Task RefreshCoreVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_xrayExePath, "version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct); // вычитываем, чтобы не заполнить буфер (ревью п.7, тот же урок)
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            var firstLine = (await stdoutTask).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "Xray")
                _coreVersion = parts[1];
        }
        catch (Exception ex)
        {
            _log($"Не удалось определить версию xray.exe ({ex.Message}) — показываю прежнюю ({_coreVersion})");
        }
    }

    public void SetLink(string linkText)
    {
        // Разбираем ВНЕ замка _gate — set-link не трогает работающий туннель,
        // это только подготовка к следующему on (план, 3.5: set-link — своя команда, не часть on).
        var parsed = LinkParser.Parse(linkText);
        // Ревью п.11: LinkParser сам НАРОЧНО принимает quic/http/h2 (parity
        // с эталонным parse_link() на Linux, план 3.1/5.7 — golden-тесты
        // требуют именно этого). Отдельная Windows-специфичная проверка
        // ПОСЛЕ разбора (план, раздел 1: "Не делаем" — в ядре нет TUN-
        // инбаунда для этих транспортов) — раньше без неё конфиг собирался,
        // xray.exe падал на разборе, пользователь через 40с получал
        // TimeoutException про адаптер, ни слова о настоящей причине.
        WindowsTransportGuard.RequireTunSupported(parsed);
        _link = parsed;
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

            // Ревью п.12: allowInsecure=1 (TLS, не REALITY — у той своё
            // доверие через pbk/sid) молча ничего не делал на
            // CoreFamily=modern (дефолт) — OutboundBuilder ставит
            // pinnedPeerCertSha256 ТОЛЬКО если он уже задан, а задавать
            // его было некому. Получаем отпечаток ДО сборки конфига (он
            // неизменяемый init-параметр BuildOptions) — тот же порядок,
            // что и Bootstrap-резолв ниже в TunnelManager.StartAsync.
            string? certPin = null;
            if (_link.Security == "tls" && _link.AllowInsecure)
            {
                certPin = await CertPinResolver.ResolveAsync(_link.Host, _link.Port, _link.Sni, ct);
                if (certPin is null)
                {
                    // Ревью п.18: раньше при неудаче (сервер недоступен в
                    // момент on, таймаут 8с у CertPinResolver) туннель всё
                    // равно поднимался — ссылка с allowInsecure=1 заведомо
                    // рассчитана на самоподписанный сертификат, без пина
                    // xray его не примет, и получалось "молча не работает",
                    // только со строкой в файле лога, которую пользователь
                    // не видит. Это должно доезжать до статуса.
                    const string msg = "allowInsecure=1 в ссылке, но не удалось получить отпечаток TLS-сертификата сервера (сервер недоступен?) — без него самоподписанный сертификат не будет принят";
                    _log(msg);
                    _error = msg;
                    SetState(TunnelState.Error);
                    throw new InvalidOperationException(msg);
                }
                _log("allowInsecure: получен отпечаток TLS-сертификата сервера (pinnedPeerCertSha256)");
            }

            _manager = new TunnelManager(new WindowsConfigOptions { Outbound = new BuildOptions { CertPin = certPin } }, _xrayExePath, _configPath, _log, _killSwitch);
            // Ревью п.9: план (3.2) требует перезапуск при смене IP сервера —
            // раньше периодического перерезолва не было вовсе, при смене
            // A-записи туннель молча продолжал стучаться в старый адрес до
            // ручного рестарта. TunnelManager сам следит за этим и просто
            // сигналит о смене — не перезапускает себя сам (он не знает
            // про _gate/пересоздание себя же самого, это дело контроллера).
            _manager.ServerAddressChanged += OnServerAddressChanged;
            try
            {
                await _manager.StartAsync(_link, ct);
                _error = null;
                SetState(TunnelState.On);
                SaveDesiredState(on: true); // ревью п.10 — переживает падение/перезапуск службы
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
            // Ревью п.9: любое явное выключение отменяет отложенный
            // автовозврат captive portal — раньше таймер включал туннель
            // обратно даже если пользователь сам выключил его во время
            // 5-минутного окна (отмена стояла только в самом
            // CaptivePortalBypassAsync — на ПОВТОРНЫЙ вызов "ещё 5 минут",
            // но не на обычный off). Здесь, а не только в
            // CaptivePortalBypassAsync — потому что OffAsync это тот же
            // путь, которым идёт и любой прямой off от пользователя.
            _captivePortalCts?.Cancel();
            _captivePortalCts = null;
            if (_state == TunnelState.Off) return;
            SetState(TunnelState.Stopping);
            await SafeStopAsync();
            _error = null;
            SetState(TunnelState.Off);
            SaveDesiredState(on: false); // ревью п.10 — переживает падение/перезапуск службы
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
        // OffAsync (ниже) сам отменяет любой предыдущий отложенный
        // автовозврат (ревью п.9) — новый cts создаём ПОСЛЕ, не до, иначе
        // эта же отмена внутри OffAsync снесла бы его собственный.
        var hadManager = _manager is not null;
        await OffAsync();
        _log(hadManager
            ? $"Captive portal: туннель выключен на {duration.TotalMinutes:F0} мин для прямого доступа, потом включится снова"
            : "Captive portal: туннель и так был выключен, снимать было нечего");

        if (!hadManager) return; // не было чем блокировать — нечего и восстанавливать

        var cts = new CancellationTokenSource();
        _captivePortalCts = cts;
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
            await RefreshCoreVersionAsync(ct);

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
                    await RefreshCoreVersionAsync(CancellationToken.None); // откатили файлы — откатываем и кэш версии
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

    // Срабатывает на фоновом Task'е внутри TunnelManager (см. его
    // PeriodicReresolveLoopAsync) — не на пути, держащем _gate, поэтому
    // отдельный Task.Run тут безопасен и не самозаблокируется на семафоре.
    private void OnServerAddressChanged()
    {
        _log("Адрес сервера изменился — перезапускаю туннель");
        _ = Task.Run(async () =>
        {
            try { await RestartAsync(CancellationToken.None); }
            catch (Exception ex) { _log($"Автоматический перезапуск после смены адреса сервера упал: {ex.Message}"); }
        });
    }

    private void SetState(TunnelState state)
    {
        _state = state;
        StatusChanged?.Invoke(GetStatus());
    }
}
