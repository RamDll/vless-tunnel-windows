using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using VlessTunnel.Core;
using VlessTunnel.Core.Models;
using VlessTunnel.Native;

namespace VlessTunnel.Service;

/// <summary>
/// Оркестрация подъёма/снятия туннеля (план, 3.2). Порядок старта и
/// остановки — строго по плану; создаваемые маршруты, адрес TUN и (если
/// включён) kill-switch снимаются в обратном порядке. Kill-switch (WFP,
/// 3.3) — за флагом <c>killSwitch</c> конструктора, MVP-объём (см.
/// doc-комментарий <see cref="VlessTunnel.Native.KillSwitch"/>). Назначение
/// DNS-сервера адаптеру (3.2 п.7, <see cref="SetTunDnsAsync"/>) и запрет
/// DNS-утечек (kill-switch, п.8) — сделаны.
/// </summary>
public sealed class TunnelManager : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly string _xrayExePath;
    private readonly string _configPath;
    private readonly WindowsConfigOptions _options;

    private Process? _xrayProcess;
    private JobObject? _job;
    private RouteWatcher? _watcher;
    private readonly List<Action> _teardown = [];

    private IPAddress? _serverIp;
    private int _tunIfIndex;
    private int _physicalIfIndex;
    private IPAddress? _physicalGateway;
    private WindowsConfigOptions? _winOptions;

    // Ревью п.13-14: раньше каждое событие смены сети запускало свой
    // Task.Run с собственным CancellationTokenSource, который отменялся
    // следующим событием — при буре событий (реконнект Wi-Fi, выход из
    // сна, переключение Wi-Fi/Ethernet — это секунды потока событий)
    // пересчёт мог не выполниться НИ РАЗУ, а окно подавления ниже,
    // самопродлеваясь на каждый удачный пересчёт, гарантированно душило
    // и следующее настоящее событие. Плюс токен отмены не был связан со
    // StopAsync — уже проснувшийся пересчёт мог добавить хост-маршрут
    // ПОСЛЕ того, как StopAsync его снял.
    //
    // Теперь — один последовательный воркер: события только сигнализируют
    // "есть работа" (семафор ёмкостью 1 — лишние сигналы схлопываются, это
    // и есть дебаунс), обрабатывает их одна задача с токеном, привязанным
    // к жизни туннеля, а StopAsync эту задачу ДОЖИДАЕТСЯ (не просто
    // отменяет), поэтому не может наложиться на собственное снятие
    // маршрутов. Плюс сам воркер, если событий давно не было, будит себя
    // по таймауту и делает полную идемпотентную сверку сети независимо от
    // того, дошло ли вообще уведомление (план, 3.9) — потерянное событие
    // перестаёт быть катастрофой.
    private readonly SemaphoreSlim _networkChangeSignal = new(0, 1);
    private CancellationTokenSource? _watchCts;
    private Task? _watchWorkerTask;

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DebounceQuiet = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DebounceMax = TimeSpan.FromSeconds(5);

    // Добавление/удаление НАШИХ ЖЕ маршрутов и адресов само порождает
    // события NotifyRouteChange2/NotifyIpInterfaceChange (это подтверждено
    // живым тестом на стенде: без этой защиты пересчёт реагировал на
    // собственные изменения и зацикливался, в итоге перекидывая
    // хост-маршрут до сервера через сам TUN — ту самую петлю, от которой
    // этот маршрут должен защищать). Поэтому реакция на СОБЫТИЕ смены сети
    // подавляется на короткое окно после любой нашей собственной мутации
    // таблицы маршрутов/адресов — но это фильтр эха ВНУТРИ воркера
    // (см. NetworkWatchWorkerAsync), а не причина пропустить периодическую
    // сверку: та выполняется по таймауту независимо от этого окна.
    private DateTime _suppressNetworkChangeUntilUtc = DateTime.MinValue;

    private void MarkSelfMutation() => _suppressNetworkChangeUntilUtc = DateTime.UtcNow.AddSeconds(2);

    private readonly bool _killSwitch;

    // Ревью п.9: план (3.2) требует перезапуск при смене IP сервера — до
    // этого единственный резолв был при самом подъёме туннеля (StartAsync,
    // п.1), при смене A-записи (переезд сервера, DNS-балансировка на его
    // стороне) туннель молча продолжал стучаться в СТАРЫЙ, уже нерабочий
    // адрес до ручного рестарта. Не хот-свап IP у живого туннеля — план
    // явно требует именно перезапуск, что проще и переиспользует уже
    // проверенный StartAsync/StopAsync, а не отдельный путь "подменить
    // адрес на лету". Интервал намеренно большой (не секунды/минуты) —
    // это подстраховка на случай переезда сервера, не поллинг; слишком
    // частый перерезолв рискует ложно сработать на DNS round-robin
    // (несколько IP на один хост, каждый ответ — валиден), если сервер
    // администратора когда-нибудь станет так настроен — известное
    // ограничение, не защита от него.
    private static readonly TimeSpan ReresolveInterval = TimeSpan.FromMinutes(10);

    /// <summary>Сигнал (не сам перезапуск — TunnelManager не знает, как
    /// пересоздать себя же самого под чужим _gate) о том, что адрес
    /// сервера изменился и туннель нужно перезапустить.</summary>
    public event Action? ServerAddressChanged;

    public TunnelManager(WindowsConfigOptions options, string xrayExePath, string configPath, Action<string> log, bool killSwitch = false)
    {
        _options = options;
        _xrayExePath = xrayExePath;
        _configPath = configPath;
        _log = log;
        _killSwitch = killSwitch;
    }

    public async Task StartAsync(ParsedLink link, CancellationToken ct)
    {
        // 1. Bootstrap-резолв (план, 3.2, п.1).
        _serverIp = await BootstrapResolver.ResolveAsync(link, ct);
        _log($"Bootstrap-резолв: {link.Host} -> {_serverIp}");

        // 2. Запомнить текущий физический шлюз/интерфейс (п.2) — ДО того,
        // как появятся собственные маршруты через TUN, которые исказят ответ.
        (_physicalGateway, _physicalIfIndex) = RouteManager.GetBestGateway(_serverIp);
        _log($"Физический шлюз: {_physicalGateway} (ifIndex={_physicalIfIndex})");

        // 3. Собрать config.json с уже разрешённым IP в vnext.address.
        var outboundOptions = new BuildOptions
        {
            VisionUdp443 = _options.Outbound.VisionUdp443,
            Mux = _options.Outbound.Mux,
            CoreFamily = _options.Outbound.CoreFamily,
            CertPin = _options.Outbound.CertPin,
            ServerAddressOverride = _serverIp.ToString(),
        };
        var winOptions = CloneWithOutbound(_options, outboundOptions);
        _winOptions = winOptions;
        var config = ConfigBuilder.BuildConfig(link, winOptions);
        await File.WriteAllTextAsync(_configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        _log($"config.json записан: {_configPath}");

        // 4. Запустить xray.exe под Job Object (умирает вместе со службой).
        //
        // ВАЖНО: RedirectStandard{Output,Error}=true создаёт анонимные
        // pipe'ы с ограниченным буфером ОС. Если их не вычитывать, xray.exe
        // рано или поздно блокируется на записи в свой же лог — а значит
        // блокируется и весь дальнейший запуск (включая поднятие TUN).
        // Это правдоподобно объясняет замеченную на стенде нестабильность
        // времени появления адаптера (от ~1 с до таймаута в 40 с): раньше
        // потоки не вычитывались вовсе. BeginOutputReadLine/BeginErrorReadLine
        // держат pipe'ы свободными постоянно, асинхронно.
        _job = new JobObject("vless-tunnel-xray");
        var xrayStart = new ProcessStartInfo(_xrayExePath, $"run -c \"{_configPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _xrayProcess = Process.Start(xrayStart) ?? throw new InvalidOperationException($"Не удалось запустить {_xrayExePath}");
        // Redact.Secrets — защита от гипотетической, но не исключённой
        // утечки: в норме xray на loglevel=warning не печатает содержимое
        // конфига, но при ошибке разбора конфига МОГ БЫ процитировать его
        // фрагмент, включая UUID (план: "секреты не попадают в вывод").
        // Чужой процесс, его вывод — не наш контракт, поэтому фильтруем
        // защитно, а не полагаемся на то, что xray никогда так не сделает.
        _xrayProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _log($"xray: {Redact.Secrets(e.Data)}"); };
        _xrayProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log($"xray[err]: {Redact.Secrets(e.Data)}"); };
        _xrayProcess.BeginOutputReadLine();
        _xrayProcess.BeginErrorReadLine();
        _job.Assign(_xrayProcess);
        _teardown.Add(() => TryKillXray());
        _log($"xray.exe запущен, pid={_xrayProcess.Id}");

        // 5. Дождаться адаптера (п.4).
        _tunIfIndex = await WaitForAdapterAsync(winOptions.TunAdapterName, TimeSpan.FromSeconds(40), ct);
        _log($"Адаптер {winOptions.TunAdapterName} поднят, ifIndex={_tunIfIndex}");

        // 6. Адрес TUN (п.5). Сразу после появления адаптера
        // CreateUnicastIpAddressEntry периодически падает с ERROR_OBJECT_
        // ALREADY_EXISTS (5010), хотя такого адреса ещё ни у кого нет —
        // тот же класс проблемы, что и с маршрутами в спайке (docs/spike.md):
        // Windows не мгновенно готов к операциям сразу после появления
        // интерфейса. Лечится так же — короткими повторами, а не фиксированной
        // паузой "на глаз".
        var (v4Addr, v4Prefix) = CidrUtil.Parse(winOptions.TunAddressV4);
        WithRetry(() => RouteManager.AddAddress(v4Addr, v4Prefix, _tunIfIndex));
        _teardown.Add(() => TrySafe(() => RouteManager.RemoveAddress(v4Addr, _tunIfIndex), "удаление TUN IPv4-адреса"));

        if (winOptions.TunAddressV6 is { Length: > 0 } v6Cidr)
        {
            var (v6Addr, v6Prefix) = CidrUtil.Parse(v6Cidr);
            WithRetry(() => RouteManager.AddAddress(v6Addr, v6Prefix, _tunIfIndex));
            _teardown.Add(() => TrySafe(() => RouteManager.RemoveAddress(v6Addr, _tunIfIndex), "удаление TUN IPv6-адреса"));
        }

        // 6'. DNS-серверы для самого TUN-адаптера — без явного назначения
        // Windows не находит, кого спросить для доменов, роутящихся в TUN
        // по /1-маршрутам ниже: разрешение имён отваливается целиком, хотя
        // сырой TCP/IP через туннель работает (не поймать иначе как живым
        // тестом на реальном клиенте — на стенде эта разница не всплывала).
        // Явного снятия не требуется — настройка гибнет вместе с адаптером,
        // когда xray.exe завершается.
        await SetTunDnsAsync(winOptions.TunAdapterName, winOptions.DnsServers);

        // 7. Маршруты (п.6): хост-маршрут до сервера через физический шлюз
        // (анти-петля), default-покрытие через TUN двумя половинками /1.
        AddHostRoute();
        AddSplitDefaultRoute(IPAddress.Parse("0.0.0.0"), 1);
        AddSplitDefaultRoute(IPAddress.Parse("128.0.0.0"), 1);
        if (winOptions.TunAddressV6 is not null)
        {
            AddSplitDefaultRoute(IPAddress.Parse("::"), 1);
            AddSplitDefaultRoute(IPAddress.Parse("8000::"), 1);
        }

        // 8. Слежение за сменой сети (Wi-Fi/кабель/сон) — пересчитать шлюз,
        // переставить хост-маршрут (план, 3.2, абзац после шага 8), плюс
        // периодическая сверка (план, 3.9).
        _watcher = new RouteWatcher();
        _watcher.NetworkChanged += OnNetworkChanged;
        _watchCts = new CancellationTokenSource();
        _watchWorkerTask = NetworkWatchWorkerAsync(_watchCts.Token);

        // 8'. Kill-switch (3.3) — последним, чтобы при остановке снимался
        // первым (строго обратный порядок): пока он есть, "молчаливая
        // утечка мимо TUN" при падении xray.exe невозможна.
        if (_killSwitch)
        {
            KillSwitch.Install(_xrayExePath, _tunIfIndex, winOptions.ExcludeLan, s => _log($"killswitch: {s}"));
            _teardown.Add(() => TrySafe(KillSwitch.Uninstall, "снятие kill-switch"));
            _log("Kill-switch включён (WFP)");
        }

        // 9. Периодический перерезолв домена сервера (см. ReresolveInterval
        // выше) — последним, чтобы не стартовать раньше, чем весь туннель
        // реально поднят.
        var reresolveCts = new CancellationTokenSource();
        _teardown.Add(() => reresolveCts.Cancel());
        _ = PeriodicReresolveLoopAsync(link, reresolveCts.Token);
    }

    private async Task PeriodicReresolveLoopAsync(ParsedLink link, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ReresolveInterval, ct);
                IPAddress newIp;
                try
                {
                    newIp = await BootstrapResolver.ResolveAsync(link, ct);
                }
                catch (Exception ex)
                {
                    _log($"Периодический перерезолв {link.Host} упал (не критично, попробую снова через {ReresolveInterval.TotalMinutes:F0} мин): {ex.Message}");
                    continue;
                }
                if (!newIp.Equals(_serverIp))
                {
                    _log($"Перерезолв {link.Host}: {_serverIp} -> {newIp}, требуется перезапуск туннеля");
                    ServerAddressChanged?.Invoke();
                    return; // дальше пересоздаст TunnelController — эта копия TunnelManager всё равно скоро остановится
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// netsh, не P/Invoke — DNS_INTERFACE_SETTINGS (netioapi.dll) заметно
    /// сложнее по разметке, чем оправдано для точечного хотфикса; netsh
    /// здесь тот же общепринятый путь, которым для этого же пользуются
    /// многие VPN-клиенты под Windows.
    /// </summary>
    private async Task SetTunDnsAsync(string adapterName, IReadOnlyList<string> dnsServers)
    {
        if (dnsServers.Count == 0) return;
        await RunNetshAsync($"interface ip set dns name=\"{adapterName}\" static {dnsServers[0]} validate=no");
        for (var i = 1; i < dnsServers.Count; i++)
            await RunNetshAsync($"interface ip add dns name=\"{adapterName}\" addr={dnsServers[i]} index={i + 1} validate=no");
        _log($"DNS TUN-адаптера: {string.Join(", ", dnsServers)}");
    }

    // Ревью п.7: WaitForExit(5000) при RedirectStandardOutput/Error БЕЗ
    // вычитывания потоков — классический deadlock-риск (netsh блокируется
    // на записи в заполненный pipe-буфер, если его никто не читает), тот
    // же класс бага, что уже ловили на xray.exe. Плюс p.ExitCode дальше
    // читался БЕЗ проверки, что WaitForExit вообще дождался (мог бросить
    // InvalidOperationException на процессе, который ещё жив). Читаем
    // потоки асинхронно ПАРАЛЛЕЛЬНО ожиданию, не после него.
    private async Task RunNetshAsync(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _log($"netsh {arguments} -> не завершился за 5с, убиваю");
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return;
        }
        if (p.ExitCode != 0)
            _log($"netsh {arguments} -> код {p.ExitCode}: {(await stderrTask).Trim()}");
        _ = stdoutTask; // стандартный вывод netsh не используется, но вычитывается — иначе именно он и переполняется
    }

    private void AddHostRoute()
    {
        var prefixLength = (byte)(_serverIp!.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        // Ревью п.10 (найдено при живом тесте самого фикса восстановления
        // состояния): после НЕштатной смерти службы (Stop-Process -Force,
        // падение, обрыв питания) хост-маршрут, добавленный СТАРЫМ
        // процессом, остаётся в таблице маршрутов — teardown-список живёт
        // только в памяти процесса и с ним же теряется. Новый процесс,
        // пытаясь поднять туннель заново, падал на CreateIpForwardEntry2
        // "уже существует" — WithRetry (ниже) на этот конкретный вызов не
        // помогает: это НЕ гонка с ОС ("ещё не готова"), а буквально уже
        // существующая запись, которая сама по себе никуда не денется.
        // Снимаем возможный осиротевший маршрут ПЕРЕД добавлением —
        // best-effort — если ничего не найдено, TrySafe просто это проглотит.
        TrySafe(() => RouteManager.RemoveRoute(_serverIp, prefixLength, _physicalGateway, _physicalIfIndex), "снятие возможного осиротевшего хост-маршрута перед добавлением");
        WithRetry(() => RouteManager.AddRoute(_serverIp, prefixLength, _physicalGateway, _physicalIfIndex, metric: 0));
        _log($"Хост-маршрут до сервера: {_serverIp}/{prefixLength} via {_physicalGateway} (ifIndex={_physicalIfIndex})");
    }

    private void RemoveHostRoute(IPAddress gateway, int ifIndex)
    {
        var prefixLength = (byte)(_serverIp!.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        TrySafe(() => RouteManager.RemoveRoute(_serverIp, prefixLength, gateway, ifIndex), "удаление хост-маршрута до сервера");
    }

    private void AddSplitDefaultRoute(IPAddress network, byte prefixLength)
    {
        WithRetry(() => RouteManager.AddRoute(network, prefixLength, nextHop: null, _tunIfIndex, metric: 0));
        _teardown.Add(() => TrySafe(() => RouteManager.RemoveRoute(network, prefixLength, null, _tunIfIndex), $"удаление маршрута {network}/{prefixLength}"));
    }

    /// <summary>
    /// Повтор с короткой паузой на ERROR_OBJECT_ALREADY_EXISTS (5010) сразу
    /// после появления интерфейса — эмпирически (живой тест на стенде)
    /// Windows не сразу готова принимать CreateUnicastIpAddressEntry/
    /// CreateIpForwardEntry2 для только что созданного интерфейса, хотя
    /// заявленного конфликтующего объекта на самом деле нет. Тот же класс
    /// проблемы, что и с "маршрут появился не сразу" в спайке (docs/spike.md) —
    /// лечится повтором с паузой, а не мгновенной единственной попыткой.
    /// </summary>
    private const int AlreadyExists = 5010;

    private void WithRetry(Action action, int attempts = 30, int delayMs = 500)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();
                MarkSelfMutation();
                return;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == AlreadyExists && i < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>
    /// Реакция на NotifyRouteChange2/NotifyIpInterfaceChange (план, 3.2:
    /// смена Wi-Fi, переподключение кабеля, сон). Колбэк стреляет на потоке
    /// ОС и может быть шумным — здесь только сигнал воркеру (см.
    /// NetworkWatchWorkerAsync), сама обработка (дебаунс, пересчёт) — там.
    /// </summary>
    private void OnNetworkChanged()
    {
        try { _networkChangeSignal.Release(); }
        catch (SemaphoreFullException)
        {
            // Необработанный сигнал уже есть в очереди — воркер и так
            // сделает сверку, как только до неё дойдёт. Семафор ёмкостью 1
            // и есть дебаунс: лишние события схлопываются в один прогон.
        }
    }

    /// <summary>
    /// Единственный потребитель сигналов о смене сети (см. комментарий у
    /// <see cref="_networkChangeSignal"/> — почему один воркер вместо
    /// Task.Run на событие). Просыпается либо по сигналу (переждав бурю
    /// событий, но не дольше <see cref="DebounceMax"/>), либо по таймауту
    /// <see cref="ReconcileInterval"/> — и тогда делает полную сверку
    /// независимо от того, было ли вообще событие (план, 3.9).
    /// </summary>
    private async Task NetworkWatchWorkerAsync(CancellationToken ct)
    {
        while (true)
        {
            bool gotSignal;
            try
            {
                gotSignal = await _networkChangeSignal.WaitAsync(ReconcileInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (gotSignal)
            {
                try
                {
                    var deadline = DateTime.UtcNow + DebounceMax;
                    while (DateTime.UtcNow < deadline)
                    {
                        // Спайк (docs/spike.md) показал, что Windows не
                        // мгновенно применяет смену маршрутов к forwarding
                        // engine — короткая пауза тишины перед пересчётом.
                        // Но НЕ бесконечно: буря событий без пауз иначе
                        // может не дать сверке случиться ни разу (п.13).
                        if (!await _networkChangeSignal.WaitAsync(DebounceQuiet, ct)) break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (DateTime.UtcNow < _suppressNetworkChangeUntilUtc)
                {
                    // Эхо нашей же недавней мутации таблицы маршрутов (см.
                    // комментарий у _suppressNetworkChangeUntilUtc) — не
                    // настоящая смена сети. Пропускаем ТОЛЬКО этот прогон,
                    // не саму сверку вообще: ближайший таймаут или следующее
                    // событие её всё равно выполнят.
                    continue;
                }
            }

            try
            {
                await ReconcileNetworkStateAsync();
            }
            catch (Exception ex)
            {
                _log($"Сверка сетевого состояния упала: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Полная идемпотентная сверка (план, 3.9: "после сна и гибернации —
    /// полная проверка маршрутов и адаптера, не только по уведомлениям").
    /// Каждая проверка сама решает, чинить ли что-то — безопасно звать на
    /// каждый тик таймера, даже если ничего не изменилось.
    /// </summary>
    private async Task ReconcileNetworkStateAsync()
    {
        if (_serverIp is null || _winOptions is null) return;

        ReconcileHostRoute();

        // Если xray.exe умер сам по себе (не через StopAsync), TUN-адаптер
        // исчезает вместе с ним, а _tunIfIndex остаётся указывать на уже
        // несуществующий интерфейс. Починить тут всё равно нечего: адаптер
        // возвращает только сам xray.exe при следующем on. Живым тестом на
        // стенде (kill xray.exe, ждать несколько циклов сверки) подтверждено:
        // ни краха, ни шторма повторов, ни утечки хост-маршрута — тот
        // продолжает поддерживаться независимо от TUN (см. ReconcileHostRoute
        // выше). Отдельную проверку "жив ли адаптер" заводить не стали:
        // NetworkInterface.GetAllNetworkInterfaces() и
        // ConvertInterfaceIndexToLuid — оба того же живого теста показали
        // ложное "жив" для уже погибшего ifIndex (отстают/не считают это
        // ошибкой), а вот сами Create*Entry2 на мёртвый ifIndex либо честно
        // проваливаются (тогда лови catch ниже), либо, судя по отсутствию
        // и исключений, и повторных попыток в логе, Windows принимает
        // запись без валидации интерфейса — в обоих случаях безопасно.
        try
        {
            ReconcileSplitDefaultRoutes();
            ReconcileTunAddresses();
            await ReconcileTunDnsAsync();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log($"Сверка TUN-состояния упала (адаптер {_winOptions.TunAdapterName} мог исчезнуть вместе с xray.exe) — {ex.Message}");
        }
    }

    private void ReconcileHostRoute()
    {
        var oldGateway = _physicalGateway!;
        var oldIfIndex = _physicalIfIndex;

        // Снять старый хост-маршрут ПЕРЕД пересчётом — иначе GetBestRoute2
        // увидит наш же (возможно, уже неверный) маршрут и вернёт его снова.
        // Делается безусловно на каждый прогон (не только когда что-то
        // видимо изменилось) — иначе застарелый, но всё ещё физически
        // присутствующий маршрут маскировал бы от GetBestRoute2 реальную
        // смену шлюза на том же интерфейсе (например, DHCP выдал новый
        // шлюз без смены ifIndex).
        RemoveHostRoute(oldGateway, oldIfIndex);

        var (newGateway, newIfIndex) = RouteManager.GetBestGateway(_serverIp!);

        // Жёсткий инвариант, а не только тайминг: анти-петлевой хост-маршрут
        // НИКОГДА не должен указывать на сам TUN — живым тестом на стенде
        // подтверждено, что окно подавления (см. _suppressNetworkChangeUntilUtc)
        // не ловит всё (например, задержанное событие NLA/Windows от самого
        // факта появления TUN-адаптера может прийти позже 2-секундного окна).
        // Без этой проверки такой ложный триггер один раз — и хост-маршрут
        // навсегда остаётся зациклен через TUN, пока не придёт следующий.
        if (newIfIndex == _tunIfIndex)
        {
            _log($"Пересчёт шлюза вернул сам TUN (ifIndex={newIfIndex}) — игнорирую как ложный, восстанавливаю прежний {oldGateway}(if={oldIfIndex})");
            _physicalGateway = oldGateway;
            _physicalIfIndex = oldIfIndex;
            AddHostRoute();
            return;
        }

        _physicalGateway = newGateway;
        _physicalIfIndex = newIfIndex;
        AddHostRoute();

        if (!_physicalGateway.Equals(oldGateway) || _physicalIfIndex != oldIfIndex)
            _log($"Сеть сменилась: шлюз {oldGateway}(if={oldIfIndex}) -> {_physicalGateway}(if={_physicalIfIndex})");
    }

    /// <summary>
    /// /1-маршруты в TUN — проверка через пробный адрес из каждой половины
    /// (GetBestRoute2 читает только локальную таблицу, пакет никуда не
    /// уходит). Адреса пробников намеренно не 127.0.0.0/8 — тот входит в
    /// 0.0.0.0/1, но у loopback всегда отдельный, более специфичный
    /// маршрут, не через TUN.
    /// </summary>
    private void ReconcileSplitDefaultRoutes()
    {
        CheckSplitHalf(IPAddress.Parse("1.0.0.0"), IPAddress.Parse("0.0.0.0"));
        CheckSplitHalf(IPAddress.Parse("129.0.0.0"), IPAddress.Parse("128.0.0.0"));
        if (_winOptions!.TunAddressV6 is not null)
        {
            CheckSplitHalf(IPAddress.Parse("1::"), IPAddress.Parse("::"));
            CheckSplitHalf(IPAddress.Parse("8001::"), IPAddress.Parse("8000::"));
        }
    }

    private void CheckSplitHalf(IPAddress probe, IPAddress network)
    {
        var (_, ifIndex) = RouteManager.GetBestGateway(probe);
        if (ifIndex == _tunIfIndex) return; // маршрут на месте

        _log($"Сверка: /1-маршрут {network}/1 через TUN пропал — восстанавливаю");
        TrySafe(() => RouteManager.RemoveRoute(network, 1, null, _tunIfIndex), $"снятие возможного осиротевшего {network}/1 перед восстановлением");
        WithRetry(() => RouteManager.AddRoute(network, 1, nextHop: null, _tunIfIndex, metric: 0));
    }

    /// <summary>Адрес(а) TUN-адаптера — сверка через System.Net.NetworkInformation
    /// (только чтение, без нового P/Invoke).</summary>
    private void ReconcileTunAddresses()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, _winOptions!.TunAdapterName, StringComparison.OrdinalIgnoreCase));
        if (nic is null) return; // адаптера целиком нет — не забота сверки, xray сам такое не переживёт

        var unicast = nic.GetIPProperties().UnicastAddresses;

        var (v4Addr, v4Prefix) = CidrUtil.Parse(_winOptions!.TunAddressV4);
        if (!unicast.Any(a => a.Address.Equals(v4Addr)))
        {
            _log($"Сверка: адрес TUN {v4Addr}/{v4Prefix} пропал — восстанавливаю");
            TrySafe(() => RouteManager.AddAddress(v4Addr, v4Prefix, _tunIfIndex), "восстановление TUN IPv4-адреса");
        }

        if (_winOptions.TunAddressV6 is { Length: > 0 } v6Cidr)
        {
            var (v6Addr, v6Prefix) = CidrUtil.Parse(v6Cidr);
            if (!unicast.Any(a => a.Address.Equals(v6Addr)))
            {
                _log($"Сверка: адрес TUN {v6Addr}/{v6Prefix} пропал — восстанавливаю");
                TrySafe(() => RouteManager.AddAddress(v6Addr, v6Prefix, _tunIfIndex), "восстановление TUN IPv6-адреса");
            }
        }
    }

    /// <summary>DNS-серверы TUN-адаптера — сверка тем же способом, что и
    /// назначение (netsh, см. SetTunDnsAsync).</summary>
    private async Task ReconcileTunDnsAsync()
    {
        if (_winOptions!.DnsServers.Length == 0) return;

        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, _winOptions.TunAdapterName, StringComparison.OrdinalIgnoreCase));
        if (nic is null) return;

        var current = nic.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToList();
        if (current.SequenceEqual(_winOptions.DnsServers)) return;

        _log($"Сверка: DNS TUN-адаптера разошёлся (было [{string.Join(", ", current)}], нужно [{string.Join(", ", _winOptions.DnsServers)}]) — переустанавливаю");
        await SetTunDnsAsync(_winOptions.TunAdapterName, _winOptions.DnsServers);
    }

    public async Task StopAsync()
    {
        if (_watcher is not null)
        {
            _watcher.NetworkChanged -= OnNetworkChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        // Ревью п.14: раньше здесь только отменялся _debounceCts, а уже
        // проснувшийся пересчёт (токен внутрь него не передавался) ничем
        // не останавливался и мог добавить хост-маршрут ПОСЛЕ того, как
        // строка ниже его сняла — маршрут оставался висеть. Теперь
        // ДОЖИДАЕМСЯ завершения воркера (а не просто отменяем сигнал) —
        // после await ниже он гарантированно не выполняет и не начнёт
        // выполнять пересчёт, поэтому RemoveHostRoute дальше не может
        // наложиться на его же AddHostRoute.
        if (_watchCts is not null)
        {
            _watchCts.Cancel();
            if (_watchWorkerTask is not null)
            {
                try { await _watchWorkerTask; }
                catch (Exception ex) { _log($"Ожидание воркера слежения за сетью упало: {ex.Message}"); }
            }
            _watchCts.Dispose();
            _watchCts = null;
            _watchWorkerTask = null;
        }

        if (_serverIp is not null && _physicalGateway is not null)
            RemoveHostRoute(_physicalGateway, _physicalIfIndex);

        // Остальное — строго в обратном порядке создания (план, 3.2: "Все
        // созданные маршруты и фильтры... снимаются по метке, не «по
        // памяти процесса»" — для этапа 2 достаточно этого стека, т.к.
        // отладочный вход живёт в одном процессе на весь сеанс on/off;
        // переживание падения СЛУЖБЫ между этими же вызовами (doctor,
        // поиск по метке OwnRouteProtocol) — этап 3/7).
        for (var i = _teardown.Count - 1; i >= 0; i--)
        {
            try { _teardown[i](); }
            catch (Exception ex) { _log($"Откат шага {i} упал: {ex.Message}"); }
        }
        _teardown.Clear();

        _job?.Dispose();
        _job = null;
        _xrayProcess = null;
    }

    private void TryKillXray()
    {
        try
        {
            if (_xrayProcess is { HasExited: false } proc)
            {
                proc.Kill(entireProcessTree: true);
                // Kill() асинхронен на уровне ОС — запрашивает завершение,
                // но не гарантирует, что процесс успел выйти и ОТПУСТИТЬ
                // СВОЙ EXE-ФАЙЛ к моменту возврата. Найдено живым тестом
                // (ревью п.5): после переноса выключения ближе к подмене
                // файлов в update-core немедленный File.Copy поверх
                // xray.exe стал падать с "используется другим процессом" —
                // раньше это маскировалось большим запасом времени между
                // OffAsync() (в самом начале) и подменой файлов (после
                // скачивания), сама гонка была всегда, просто не проявлялась.
                if (!proc.WaitForExit(5000))
                    _log("xray.exe не завершился за 5с после Kill() — возможна гонка при следующей подмене файлов");
            }
        }
        catch (Exception ex) { _log($"Не удалось завершить xray.exe: {ex.Message}"); }
    }

    private void TrySafe(Action action, string what)
    {
        try { action(); }
        catch (Exception ex) { _log($"{what} не удалась (не критично при откате): {ex.Message}"); }
        finally { MarkSelfMutation(); }
    }

    private static async Task<int> WaitForAdapterAsync(string adapterName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase)
                                      && n.OperationalStatus == OperationalStatus.Up);
            if (nic is not null)
            {
                var props = nic.GetIPProperties();
                var index = props.GetIPv4Properties()?.Index ?? props.GetIPv6Properties()?.Index;
                if (index is not null) return index.Value;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
        throw new TimeoutException($"Адаптер \"{adapterName}\" не поднялся за {timeout}");
    }

    private static WindowsConfigOptions CloneWithOutbound(WindowsConfigOptions o, BuildOptions outbound) => new()
    {
        Outbound = outbound,
        SocksPort = o.SocksPort,
        HttpPort = o.HttpPort,
        ExcludeLan = o.ExcludeLan,
        LogLevel = o.LogLevel,
        AccessLog = o.AccessLog,
        LogDir = o.LogDir,
        TunAddressV4 = o.TunAddressV4,
        TunAddressV6 = o.TunAddressV6,
        Mtu = o.Mtu,
        TunStack = o.TunStack,
        TunAdapterName = o.TunAdapterName,
    };

    public async ValueTask DisposeAsync() => await StopAsync();
}
