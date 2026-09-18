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
/// остановки — строго по плану; создаваемые маршруты и адрес TUN снимаются
/// в обратном порядке. Kill-switch (WFP, 3.3) и назначение DNS-сервера
/// адаптеру (3.2 п.7) сюда сознательно НЕ входят — это этап 3, у этапа 2
/// по плану (раздел 4) более узкий критерий приёмки: on/off, переживание
/// смены Wi-Fi/кабеля, чистый откат маршрутов после off.
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
    private CancellationTokenSource? _debounceCts;
    private readonly Lock _gatewayLock = new();

    // Добавление/удаление НАШИХ ЖЕ маршрутов и адресов само порождает
    // события NotifyRouteChange2/NotifyIpInterfaceChange (это подтверждено
    // живым тестом на стенде: без этой защиты RecomputeHostRoute реагировал
    // на собственные изменения и зацикливался, в итоге перекидывая
    // хост-маршрут до сервера через сам TUN — ту самую петлю, от которой
    // этот маршрут должен защищать). Поэтому реакция на смену сети
    // подавляется на короткое окно после любой нашей собственной мутации
    // таблицы маршрутов/адресов.
    private DateTime _suppressNetworkChangeUntilUtc = DateTime.MinValue;

    private void MarkSelfMutation() => _suppressNetworkChangeUntilUtc = DateTime.UtcNow.AddSeconds(2);

    public TunnelManager(WindowsConfigOptions options, string xrayExePath, string configPath, Action<string> log)
    {
        _options = options;
        _xrayExePath = xrayExePath;
        _configPath = configPath;
        _log = log;
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
        var config = ConfigBuilder.BuildConfig(link, winOptions);
        await File.WriteAllTextAsync(_configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
        _log($"config.json записан: {_configPath}");

        // 4. Запустить xray.exe под Job Object (умирает вместе со службой).
        _job = new JobObject("vless-tunnel-xray");
        _xrayProcess = Process.Start(new ProcessStartInfo(_xrayExePath, $"run -c \"{_configPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Не удалось запустить {_xrayExePath}");
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
        // переставить хост-маршрут (план, 3.2, абзац после шага 8).
        _watcher = new RouteWatcher();
        _watcher.NetworkChanged += OnNetworkChanged;

        // Kill-switch (3.3) — намеренно не здесь, см. doc-комментарий класса.
    }

    private void AddHostRoute()
    {
        var prefixLength = (byte)(_serverIp!.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
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

    private void WithRetry(Action action, int attempts = 5, int delayMs = 300)
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
    /// смена Wi-Fi, переподключение кабеля, сон). Колбэк стреляет на
    /// потоке ОС и может быть шумным — дебаунс на 1 секунду, плюс сама
    /// пауза перед пересчётом: спайк (docs/spike.md) показал, что Windows
    /// не мгновенно применяет смену маршрутов к forwarding engine.
    /// </summary>
    private void OnNetworkChanged()
    {
        lock (_gatewayLock)
        {
            _debounceCts?.Cancel();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                    RecomputeHostRoute();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log($"Пересчёт хост-маршрута после смены сети упал: {ex.Message}");
                }
            }, cts.Token);
        }
    }

    private void RecomputeHostRoute()
    {
        if (_serverIp is null) return;
        if (DateTime.UtcNow < _suppressNetworkChangeUntilUtc)
        {
            // Эхо нашей же недавней мутации таблицы маршрутов (см. комментарий
            // у _suppressNetworkChangeUntilUtc) — не настоящая смена сети.
            return;
        }
        var oldGateway = _physicalGateway!;
        var oldIfIndex = _physicalIfIndex;

        // Снять старый хост-маршрут ПЕРЕД пересчётом — иначе GetBestRoute2
        // увидит наш же (возможно, уже неверный) маршрут и вернёт его снова.
        RemoveHostRoute(oldGateway, oldIfIndex);

        (_physicalGateway, _physicalIfIndex) = RouteManager.GetBestGateway(_serverIp);
        AddHostRoute();

        if (!_physicalGateway.Equals(oldGateway) || _physicalIfIndex != oldIfIndex)
            _log($"Сеть сменилась: шлюз {oldGateway}(if={oldIfIndex}) -> {_physicalGateway}(if={_physicalIfIndex})");
    }

    public Task StopAsync()
    {
        _debounceCts?.Cancel();
        if (_watcher is not null)
        {
            _watcher.NetworkChanged -= OnNetworkChanged;
            _watcher.Dispose();
            _watcher = null;
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

        return Task.CompletedTask;
    }

    private void TryKillXray()
    {
        try
        {
            if (_xrayProcess is { HasExited: false })
                _xrayProcess.Kill(entireProcessTree: true);
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
