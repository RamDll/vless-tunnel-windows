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
    // это подстраховка на случай переезда сервера, не поллинг.
    //
    // Ревью п.17: DNS round-robin (несколько IP на один хост, порядок
    // ответа меняется от запроса к запросу) раньше ложно срабатывал —
    // PeriodicReresolveLoopAsync сравнивал ТЕКУЩИЙ IP с ПЕРВЫМ адресом
    // нового ответа, и при смене порядка получался перезапуск туннеля на
    // ровном месте каждый цикл. Исправлено: сравнение с ВСЕМ набором
    // адресов (BootstrapResolver.ResolveSetAsync), перезапуск — только
    // если текущий IP пропал из набора совсем.
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
        // FindBestPhysicalGateway (не GetBestGateway) — тот же метод, что и
        // сверка ниже (ревью п.22): TUN ещё не поднят на этом шаге, исключать
        // из кандидатов нечего, но метод один и тот же ради единообразия.
        (_physicalGateway, _physicalIfIndex) = FindBestPhysicalGateway(_serverIp);
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

        // 4-5. Запустить xray.exe и дождаться адаптера (план, 3.2, п.4) — с
        // повтором на транзиентную гонку пересоздания wintun-адаптера
        // (см. LaunchXrayAndWaitForAdapterAsync и живой тест там же).
        _tunIfIndex = await LaunchXrayAndWaitForAdapterAsync(winOptions.TunAdapterName, ct);
        _log($"Адаптер {winOptions.TunAdapterName} поднят, ifIndex={_tunIfIndex}");

        // 6. Адрес TUN (п.5). Сразу после появления адаптера
        // CreateUnicastIpAddressEntry периодически падает с ERROR_OBJECT_
        // ALREADY_EXISTS (5010), хотя такого адреса ещё ни у кого нет —
        // тот же класс проблемы, что и с маршрутами в спайке (docs/spike.md):
        // Windows не мгновенно готов к операциям сразу после появления
        // интерфейса. Лечится так же — короткими повторами, а не фиксированной
        // паузой "на глаз".
        var (v4Addr, v4Prefix) = CidrUtil.Parse(winOptions.TunAddressV4);
        WithRetry(() => RouteManager.AddAddress(v4Addr, v4Prefix, _tunIfIndex), ct);
        _teardown.Add(() => TrySafe(() => RouteManager.RemoveAddress(v4Addr, _tunIfIndex), "удаление TUN IPv4-адреса"));

        if (winOptions.TunAddressV6 is { Length: > 0 } v6Cidr)
        {
            var (v6Addr, v6Prefix) = CidrUtil.Parse(v6Cidr);
            WithRetry(() => RouteManager.AddAddress(v6Addr, v6Prefix, _tunIfIndex), ct);
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
        AddHostRoute(_physicalGateway, _physicalIfIndex, ct);
        AddSplitDefaultRoute(IPAddress.Parse("0.0.0.0"), 1, ct);
        AddSplitDefaultRoute(IPAddress.Parse("128.0.0.0"), 1, ct);
        if (winOptions.TunAddressV6 is not null)
        {
            AddSplitDefaultRoute(IPAddress.Parse("::"), 1, ct);
            AddSplitDefaultRoute(IPAddress.Parse("8000::"), 1, ct);
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
                IReadOnlyList<IPAddress> newSet;
                try
                {
                    newSet = await BootstrapResolver.ResolveSetAsync(link, ct);
                }
                catch (Exception ex)
                {
                    _log($"Периодический перерезолв {link.Host} упал (не критично, попробую снова через {ReresolveInterval.TotalMinutes:F0} мин): {ex.Message}");
                    continue;
                }

                // Ревью п.17: сравниваем ТЕКУЩИЙ IP с ВСЕМ набором, не с
                // "первым адресом нового ответа" — DNS round-robin меняет
                // порядок записей от запроса к запросу, у хоста с
                // несколькими A-записями сравнение только по первому
                // адресу перезапускало бы туннель каждый цикл на ровном
                // месте, даже когда набор адресов сервера не менялся.
                // Перезапуск нужен, только если ТЕКУЩИЙ адрес пропал из
                // набора совсем (сервер реально переехал/сменил IP).
                if (_serverIp is not null && newSet.Contains(_serverIp))
                    continue;

                var newIp = newSet.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? newSet[0];
                _log($"Перерезолв {link.Host}: {_serverIp} пропал из набора адресов ({string.Join(", ", newSet)}) -> {newIp}, требуется перезапуск туннеля");
                ServerAddressChanged?.Invoke();
                return; // дальше пересоздаст TunnelController — эта копия TunnelManager всё равно скоро остановится
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

    private void AddHostRoute(IPAddress gateway, int ifIndex, CancellationToken ct, int attempts = StartupRetryAttempts, int delayMs = StartupRetryDelayMs)
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
        TrySafe(() => RouteManager.RemoveRoute(_serverIp, prefixLength, gateway, ifIndex), "снятие возможного осиротевшего хост-маршрута перед добавлением");
        WithRetry(() => RouteManager.AddRoute(_serverIp, prefixLength, gateway, ifIndex, metric: 0), ct, attempts, delayMs);
        _log($"Хост-маршрут до сервера: {_serverIp}/{prefixLength} via {gateway} (ifIndex={ifIndex})");
    }

    private void RemoveHostRoute(IPAddress gateway, int ifIndex)
    {
        var prefixLength = (byte)(_serverIp!.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        TrySafe(() => RouteManager.RemoveRoute(_serverIp, prefixLength, gateway, ifIndex), "удаление хост-маршрута до сервера");
    }

    private void AddSplitDefaultRoute(IPAddress network, byte prefixLength, CancellationToken ct)
    {
        WithRetry(() => RouteManager.AddRoute(network, prefixLength, nextHop: null, _tunIfIndex, metric: 0), ct);
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

    // Ревью п.20 (заодно с п.22): WithRetry раньше не получал токен отмены
    // вовсе и спал через Thread.Sleep — если StopAsync отменял воркер
    // (см. _watchCts) ровно во время серии повторов, off вставал на всё
    // оставшееся время повторов (до 15с на старом бюджете), под общим
    // _gate контроллера — то есть повисала вся служба для всех клиентов.
    // Теперь токен проверяется на каждой паузе (Task.Delay(ct), не
    // Thread.Sleep), и у сверочного пути (ReconcileHostRoute/
    // CheckSplitHalf, вызываются из воркера) — свой, намного более
    // короткий бюджет: 15с там не нужны и вредны, сверка и так повторится
    // на следующем цикле. Стартовый бюджет (30×500мс, ожидание только
    // что созданного интерфейса при StartAsync) не менялся.
    private const int StartupRetryAttempts = 30;
    private const int StartupRetryDelayMs = 500;
    private const int ReconcileRetryAttempts = 5;
    private const int ReconcileRetryDelayMs = 200;

    private void WithRetry(Action action, CancellationToken ct, int attempts = StartupRetryAttempts, int delayMs = StartupRetryDelayMs)
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
                Task.Delay(delayMs, ct).GetAwaiter().GetResult();
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
                await ReconcileNetworkStateAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // StopAsync отменил ровно во время сверки (см. ревью п.20) —
                // выходим тихо, это не сбой, а штатная остановка.
                return;
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
    /// каждый тик таймера, даже если ничего не изменилось. ct — токен
    /// жизни воркера (см. _watchCts): StopAsync его отменяет и ДОЖИДАЕТСЯ
    /// воркер, поэтому любой WithRetry внутри сверки должен реагировать на
    /// него быстро, а не спать полный бюджет (ревью п.20).
    /// </summary>
    private async Task ReconcileNetworkStateAsync(CancellationToken ct)
    {
        if (_serverIp is null || _winOptions is null) return;

        ReconcileHostRoute(ct);

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
            ReconcileSplitDefaultRoutes(ct);
            ReconcileTunAddresses();
            await ReconcileTunDnsAsync();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _log($"Сверка TUN-состояния упала (адаптер {_winOptions.TunAdapterName} мог исчезнуть вместе с xray.exe) — {ex.Message}");
        }
    }

    /// <summary>
    /// Ревью п.22: раньше физический шлюз/интерфейс пересчитывался через
    /// НЕограниченный GetBestRoute2 (interfaceIndex=0) — пока туннель
    /// поднят, в таблице уже есть 0.0.0.0/1 и 128.0.0.0/1 через TUN, а
    /// Windows выбирает маршрут СНАЧАЛА по длине префикса, потом по
    /// метрике: /1 длиннее любого физического /0-default, поэтому
    /// неограниченный поиск для ЛЮБОГО адреса (в частности — для самого
    /// сервера, как только его /32-хост-маршрут снят перед пересчётом)
    /// ГАРАНТИРОВАННО возвращал TUN, не изредка. Старый "жёсткий
    /// инвариант" ниже поэтому срабатывал не как редкая страховка от
    /// гонки с задержанным событием NLA (как было написано в комментарии —
    /// объяснение оказалось неверным), а КАЖДЫЙ раз: реальная смена сети
    /// не детектировалась НИКОГДА, пока туннель включён. Подтверждено
    /// живым тестом: ручное снятие хост-маршрута на поднятом туннеле
    /// стабильно, не изредка, даёт "вернул сам TUN".
    ///
    /// Правильный способ — ограничить поиск КОНКРЕТНЫМ интерфейсом
    /// (см. RouteManager.GetBestRouteOnInterface): тогда TUN физически не
    /// может подменить ответ, раз он не в кандидатах. Перебираем поднятые
    /// физические интерфейсы с IPv4-шлюзом (TUN исключён явно), для
    /// каждого спрашиваем "будь у нас только этот интерфейс, как бы ты
    /// пошёл до destination" — нашёлся маршрут именно на нём, это
    /// кандидат. Между кандидатами (одновременно поднятые Ethernet и
    /// Wi-Fi) выбираем как сама Windows — по минимальной сумме метрики
    /// маршрута и метрики интерфейса (GetIpInterfaceEntry), не "первый
    /// попавшийся": просто первый адаптер в списке не годится именно в
    /// этом сценарии.
    /// </summary>
    private (IPAddress Gateway, int InterfaceIndex) FindBestPhysicalGateway(IPAddress destination)
    {
        (IPAddress Gateway, int IfIndex, long Score)? best = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Живым тестом поймано: GetIPv4Properties()/GetIPProperties() на
            // некоторых интерфейсах (виртуальные/псевдо-адаптеры без полной
            // IPv4-конфигурации) не просто возвращают null, а БРОСАЮТ
            // NetworkInformationException — один такой интерфейс не должен
            // ронять весь пересчёт шлюза, просто не кандидат.
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var props = nic.GetIPProperties();
                var ifIndex = props.GetIPv4Properties()?.Index;
                if (ifIndex is null || ifIndex.Value == _tunIfIndex) continue;
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) continue;

                IPAddress nextHop;
                uint routeMetric;
                try
                {
                    (nextHop, routeMetric) = RouteManager.GetBestRouteOnInterface(destination, ifIndex.Value);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == RouteManager.ErrorNotFound)
                {
                    continue; // маршрута до destination именно через этот интерфейс нет — не кандидат
                }

                uint ifMetric;
                try
                {
                    ifMetric = RouteManager.GetInterfaceMetric(ifIndex.Value);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    continue; // не удалось получить метрику интерфейса — не рискуем, пропускаем как кандидата
                }

                var score = (long)routeMetric + ifMetric;
                if (best is null || score < best.Value.Score)
                    best = (nextHop, ifIndex.Value, score);
            }
            catch (NetworkInformationException)
            {
                continue; // интерфейс не даёт себя нормально опросить — не кандидат, не фатально
            }
        }

        if (best is null)
            throw new InvalidOperationException($"Не найден ни один физический интерфейс с маршрутом до {destination}");
        return (best.Value.Gateway, best.Value.IfIndex);
    }

    private void ReconcileHostRoute(CancellationToken ct)
    {
        (IPAddress Gateway, int IfIndex) desired;
        try
        {
            desired = FindBestPhysicalGateway(_serverIp!);
        }
        catch (Exception ex)
        {
            _log($"Не удалось пересчитать физический шлюз до сервера — {ex.Message}. Оставляю прежний {_physicalGateway}(if={_physicalIfIndex}), повторю на следующем цикле.");
            return;
        }

        // Страховка на случай бага в фильтрации кандидатов выше — TUN туда
        // попасть не должен ни при каких обстоятельствах (в отличие от
        // старого кода, это больше не единственная защита, см. комментарий
        // у FindBestPhysicalGateway), но если вдруг, лучше явно отказаться
        // и оставить прежнее состояние, чем зациклить хост-маршрут через
        // сам туннель.
        if (desired.IfIndex == _tunIfIndex)
        {
            _log($"FindBestPhysicalGateway вернул сам TUN (ifIndex={desired.IfIndex}) — это баг в фильтрации кандидатов, игнорирую и оставляю прежний шлюз {_physicalGateway}(if={_physicalIfIndex}).");
            return;
        }

        // "Есть на самом деле" — БЕЗ ограничения интерфейсом: если хост-
        // маршрут реально в таблице, его /32 длиннее любого /1 через TUN и
        // однозначно побеждает без всякой путаницы (в отличие от поиска
        // "какой шлюз должен быть", здесь неограниченный GetBestGateway —
        // ровно та проверка, которая нужна: есть ли уже наш маршрут). Если
        // маршрута нет вовсе (кто-то снял руками, либо старый шлюз мёртв
        // после смены сети), самый длинный ПОДХОДЯЩИЙ префикс — один из /1
        // через TUN, и это сигнал, что таблицу нужно чинить.
        var (actualGateway, actualIfIndex) = RouteManager.GetBestGateway(_serverIp!);
        if (actualIfIndex != _tunIfIndex && actualGateway.Equals(desired.Gateway) && actualIfIndex == desired.IfIndex)
            return; // и совпадает с ожидаемым, и реально в таблице — трогать нечего (закрывает и п.19)

        var oldGateway = _physicalGateway!;
        var oldIfIndex = _physicalIfIndex;
        RemoveHostRoute(oldGateway, oldIfIndex); // best-effort (TrySafe внутри) — не страшно, если там уже пусто

        try
        {
            AddHostRoute(desired.Gateway, desired.IfIndex, ct, ReconcileRetryAttempts, ReconcileRetryDelayMs);
        }
        catch (Exception ex)
        {
            _log($"Не удалось добавить хост-маршрут через {desired.Gateway}(if={desired.IfIndex}) — {ex.Message}. Хост-маршрут временно отсутствует, повторю на следующем цикле.");
            return; // _physicalGateway/_physicalIfIndex НЕ обновляем — следующий цикл пересчитает заново
        }

        var changed = !desired.Gateway.Equals(oldGateway) || desired.IfIndex != oldIfIndex;
        _physicalGateway = desired.Gateway;
        _physicalIfIndex = desired.IfIndex;
        if (changed)
            _log($"Сеть сменилась: шлюз {oldGateway}(if={oldIfIndex}) -> {_physicalGateway}(if={_physicalIfIndex})");
    }

    /// <summary>
    /// /1-маршруты в TUN — проверка через пробный адрес из каждой половины
    /// (GetBestRoute2 читает только локальную таблицу, пакет никуда не
    /// уходит). Адреса пробников намеренно не 127.0.0.0/8 — тот входит в
    /// 0.0.0.0/1, но у loopback всегда отдельный, более специфичный
    /// маршрут, не через TUN.
    /// </summary>
    private void ReconcileSplitDefaultRoutes(CancellationToken ct)
    {
        CheckSplitHalf(IPAddress.Parse("1.0.0.0"), IPAddress.Parse("0.0.0.0"), ct);
        CheckSplitHalf(IPAddress.Parse("129.0.0.0"), IPAddress.Parse("128.0.0.0"), ct);
        if (_winOptions!.TunAddressV6 is not null)
        {
            CheckSplitHalf(IPAddress.Parse("1::"), IPAddress.Parse("::"), ct);
            CheckSplitHalf(IPAddress.Parse("8001::"), IPAddress.Parse("8000::"), ct);
        }
    }

    private void CheckSplitHalf(IPAddress probe, IPAddress network, CancellationToken ct)
    {
        var (_, ifIndex) = RouteManager.GetBestGateway(probe);
        if (ifIndex == _tunIfIndex) return; // маршрут на месте

        _log($"Сверка: /1-маршрут {network}/1 через TUN пропал — восстанавливаю");
        TrySafe(() => RouteManager.RemoveRoute(network, 1, null, _tunIfIndex), $"снятие возможного осиротевшего {network}/1 перед восстановлением");
        WithRetry(() => RouteManager.AddRoute(network, 1, nextHop: null, _tunIfIndex, metric: 0), ct, ReconcileRetryAttempts, ReconcileRetryDelayMs);
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

    // Живым тестом на стенде подтверждено: быстрый цикл off->on изредка
    // (~30-40% в тесте из 20 подряд, воспроизводится ОДИНАКОВО что до,
    // что после фикса 13-14 — это не регрессия, а давно существующий
    // класс проблемы) даёт "Адаптер \"xray0\" не поднялся за 40с". Живая
    // причина — не в нашем коде ожидания: xray.exe сам умирает куда
    // раньше (~15 с) с "Failed to setup adapter"/"Cannot create a file
    // when that file already exists" — Kill() у предыдущего запуска (см.
    // TryKillXray) обрывает xray.exe без штатного закрытия сессии wintun,
    // а PnP-объект адаптера (SWD\WINTUN\...; подтверждено Get-PnpDevice —
    // он остаётся с тем же InstanceId между циклами, в отличие от сетевого
    // интерфейса, который исчезает мгновенно) освобождается ОС асинхронно
    // и не всегда успевает к следующему запуску. Ждать дольше бессмысленно
    // (xray уже мёртв) — гонка транзиентная, повтор всего запуска xray.exe
    // почти всегда помогает.
    // Живой тест пользователя (первый запуск на свежеустановленной машине,
    // ранее wintun-адаптер на ней ни разу не поднимался): все 3 попытки
    // подряд упали с тем же кодом выхода 23 (~50с суммарно), помог только
    // ручной повтор ЕЩЁ через ~52с. Прежний живой тест 20 циклов on/off
    // (см. PLAN-windows.md, "Нестабильность wintun-адаптера") показывал
    // 0/20 отказов при 3 попытках, но тот тест гонял УЖЕ установленный
    // драйвер/PnP-объект — вероятно, самое первое в жизни машины создание
    // wintun-адаптера тратит время ещё и на становление самого драйвера,
    // не только на освобождение объекта от предыдущего запуска, и не
    // укладывается в прежний бюджет попыток. Поднято с 3 до 6 (~100с
    // бюджет вместо ~50с) — тот же принцип (быстрый явный отказ + повтор
    // всего запуска), просто с запасом под этот более медленный случай.
    private const int MaxXrayLaunchAttempts = 6;

    private async Task<int> LaunchXrayAndWaitForAdapterAsync(string adapterName, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            StartXrayProcess();
            try
            {
                return await WaitForAdapterAsync(_xrayProcess!, adapterName, TimeSpan.FromSeconds(40), ct);
            }
            catch (XrayExitedEarlyException ex) when (attempt < MaxXrayLaunchAttempts)
            {
                _log($"xray.exe завершился раньше поднятия адаптера (попытка {attempt}/{MaxXrayLaunchAttempts}, похоже на гонку пересоздания wintun-адаптера) — {ex.Message}. Повтор через 2с.");
                RetireFailedXrayAttempt();
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private void StartXrayProcess()
    {
        // ВАЖНО: RedirectStandard{Output,Error}=true создаёт анонимные
        // pipe'ы с ограниченным буфером ОС. Если их не вычитывать, xray.exe
        // рано или поздно блокируется на записи в свой же лог — а значит
        // блокируется и весь дальнейший запуск (включая поднятие TUN).
        // BeginOutputReadLine/BeginErrorReadLine держат pipe'ы свободными
        // постоянно, асинхронно.
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
    }

    // Откатывает именно ПОСЛЕДНЮЮ (провалившуюся) попытку запуска —
    // единственную запись в _teardown, добавленную StartXrayProcess этой
    // попытки, не трогая ничего, что было накоплено раньше.
    private void RetireFailedXrayAttempt()
    {
        var last = _teardown[^1];
        _teardown.RemoveAt(_teardown.Count - 1);
        try { last(); }
        catch (Exception ex) { _log($"Откат неудачной попытки запуска xray.exe упал: {ex.Message}"); }
        _job?.Dispose();
        _job = null;
        _xrayProcess = null;
    }

    private sealed class XrayExitedEarlyException(string message) : Exception(message);

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

    private static async Task<int> WaitForAdapterAsync(Process xrayProcess, string adapterName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // xray.exe у гонки пересоздания wintun-адаптера падает сам
            // (~15с), задолго до нашего 40-секундного таймаута — ждать
            // дальше бессмысленно, а быстрый выход с понятной причиной
            // даёт LaunchXrayAndWaitForAdapterAsync шанс сразу повторить
            // попытку, а не тратить оставшееся время впустую.
            if (xrayProcess.HasExited)
                throw new XrayExitedEarlyException($"xray.exe завершился раньше, чем поднялся адаптер \"{adapterName}\" (код выхода {xrayProcess.ExitCode})");

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

    // Ревью п.21: жёсткий Kill() у предыдущего xray.exe (см. TryKillXray)
    // не даёт ему штатно закрыть сессию wintun — коллизия "file already
    // exists" при пересоздании адаптера С ТЕМ ЖЕ ИМЕНЕМ изредка возникает
    // (~треть циклов в живом тесте), и лечится повтором всего запуска
    // (LaunchXrayAndWaitForAdapterAsync), но это лечит симптом (~19с вместо
    // ~1с), не причину. Настоящее грациозное завершение (CTRL_BREAK перед
    // Kill()) потребовало бы обходить Process.Start целиком — CreateNoWindow
    // означает, что у xray.exe вообще НЕТ консоли (не "скрытая", а именно
    // никакой), а без своей консоли/process group к нему не подсоединиться
    // и не отправить консольное событие, не рискуя задеть саму службу,
    // делящую то же самое; плюс неизвестно, отреагирует ли рантайм Go
    // внутри xray-core на такой сигнал штатным закрытием wintun вообще —
    // непроверяемое допущение поверх рискованной переделки самого запуска
    // процесса. Альтернатива из того же ревью (чередовать имя адаптера)
    // устраняет коллизию МЕХАНИЧЕСКИ, без каких-либо ставок на поведение
    // xray-core: если предыдущий адаптер того же имени ещё не до конца
    // освобождён, новый с ДРУГИМ именем просто не с чем сталкиваться.
    // Статическое поле — переживает пересоздание TunnelManager между
    // on/off (TunnelController создаёт новый экземпляр на каждый on).
    private static int _tunAdapterNameGeneration;

    private static string ChooseTunAdapterName(string configuredName) =>
        System.Threading.Interlocked.Increment(ref _tunAdapterNameGeneration) % 2 == 0
            ? configuredName
            : configuredName + "b";

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
        TunAdapterName = ChooseTunAdapterName(o.TunAdapterName),
    };

    public async ValueTask DisposeAsync() => await StopAsync();
}
