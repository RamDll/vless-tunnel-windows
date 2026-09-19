using System.ComponentModel;
using System.Net;
using VlessTunnel.Native.Interop;

namespace VlessTunnel.Native;

/// <summary>
/// Высокоуровневая обёртка над P/Invoke IP Helper (план, 3.2, шаги 5-6):
/// адрес и маршруты TUN-адаптера, поиск текущего физического шлюза.
/// Методы бросают <see cref="Win32Exception"/> с человекочитаемым текстом
/// Windows на ошибке — вызывающий код (TunnelManager) решает, откатывать
/// уже сделанное или нет.
/// </summary>
public static class RouteManager
{
    public static void AddAddress(IPAddress address, byte prefixLength, int interfaceIndex)
    {
        var row = MIB_UNICASTIPADDRESS_ROW.ForAddress(address, (uint)interfaceIndex, prefixLength);
        var err = IpHelper.CreateUnicastIpAddressEntry(ref row);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"CreateUnicastIpAddressEntry({address}/{prefixLength}, ifIndex={interfaceIndex}) failed");
    }

    public static void RemoveAddress(IPAddress address, int interfaceIndex)
    {
        var row = MIB_UNICASTIPADDRESS_ROW.ForAddress(address, (uint)interfaceIndex, 0);
        var err = IpHelper.DeleteUnicastIpAddressEntry(ref row);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"DeleteUnicastIpAddressEntry({address}, ifIndex={interfaceIndex}) failed");
    }

    public static void AddRoute(IPAddress destinationNetwork, byte prefixLength, IPAddress? nextHop, int interfaceIndex, uint metric = 0)
    {
        var row = MIB_IPFORWARD_ROW2.ForRoute(destinationNetwork, prefixLength, nextHop, (uint)interfaceIndex, metric);
        var err = IpHelper.CreateIpForwardEntry2(ref row);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"CreateIpForwardEntry2({destinationNetwork}/{prefixLength} via {nextHop?.ToString() ?? "on-link"}, ifIndex={interfaceIndex}) failed");
    }

    public static void RemoveRoute(IPAddress destinationNetwork, byte prefixLength, IPAddress? nextHop, int interfaceIndex)
    {
        var row = MIB_IPFORWARD_ROW2.ForRoute(destinationNetwork, prefixLength, nextHop, (uint)interfaceIndex, 0);
        var err = IpHelper.DeleteIpForwardEntry2(ref row);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"DeleteIpForwardEntry2({destinationNetwork}/{prefixLength} via {nextHop?.ToString() ?? "on-link"}, ifIndex={interfaceIndex}) failed");
    }

    /// <summary>
    /// Текущий лучший маршрут (и, соответственно, физический шлюз/интерфейс)
    /// до указанного адреса — до поднятия TUN и после каждой смены сети
    /// (план, 3.2, п.2 и слежение за NotifyRouteChange2/NotifyIpInterfaceChange).
    /// </summary>
    public static (IPAddress Gateway, int InterfaceIndex) GetBestGateway(IPAddress destination)
    {
        var dest = SOCKADDR_INET.FromIpAddress(destination);
        var err = IpHelper.GetBestRoute2(0, 0, 0, ref dest, 0, out var bestRoute, out _);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"GetBestRoute2({destination}) failed");
        return (bestRoute.NextHop.ToIpAddress(), (int)bestRoute.InterfaceIndex);
    }

    /// <summary>Win32-код ERROR_NOT_FOUND — ожидаемый, не аварийный исход
    /// <see cref="GetBestRouteOnInterface"/> (ревью п.22): "маршрута до
    /// destination именно через этот интерфейс нет", не сбой.</summary>
    public const int ErrorNotFound = (int)IpHelper.ERROR_NOT_FOUND;

    /// <summary>
    /// GetBestRoute2, ограниченный КОНКРЕТНЫМ интерфейсом (ревью п.22).
    /// Без этого ограничения (interfaceIndex=0, как в <see cref="GetBestGateway"/>)
    /// поиск идёт по ВСЕЙ таблице маршрутов — пока поднят TUN, там уже
    /// есть /1-маршруты через него, которые Windows выбирает раньше
    /// физического /0-default просто потому, что префикс длиннее, вне
    /// зависимости от метрики. С заданным interfaceIndex поиск ведётся
    /// строго в его пределах — TUN физически не может оказаться в
    /// кандидатах, раз он не является запрошенным интерфейсом.
    /// Бросает Win32Exception с кодом <see cref="ErrorNotFound"/>, если
    /// маршрута до destination через ЭТОТ интерфейс нет — вызывающий код
    /// (перебор кандидатов) обрабатывает это как "не кандидат", не как
    /// ошибку.
    /// </summary>
    public static (IPAddress NextHop, uint Metric) GetBestRouteOnInterface(IPAddress destination, int interfaceIndex)
    {
        var dest = SOCKADDR_INET.FromIpAddress(destination);
        var err = IpHelper.GetBestRoute2(0, (uint)interfaceIndex, 0, ref dest, 0, out var bestRoute, out _);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"GetBestRoute2({destination}, ifIndex={interfaceIndex}) failed");
        return (bestRoute.NextHop.ToIpAddress(), bestRoute.Metric);
    }

    /// <summary>
    /// Метрика интерфейса (ревью п.22) — интерфейсная составляющая
    /// эффективной метрики маршрута (документация MIB_IPINTERFACE_ROW:
    /// "actual route metric... is the summation of the route metric...
    /// and the interface metric"). Нужна, чтобы выбирать между НЕСКОЛЬКИМИ
    /// одновременно поднятыми физическими интерфейсами (Ethernet и Wi-Fi
    /// разом) так же, как это делает сама Windows — по минимальной сумме,
    /// а не "первый попавшийся".
    /// </summary>
    public static uint GetInterfaceMetric(int interfaceIndex)
    {
        var row = MIB_IPINTERFACE_ROW.ForQuery((uint)interfaceIndex);
        var err = IpHelper.GetIpInterfaceEntry(ref row);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"GetIpInterfaceEntry(ifIndex={interfaceIndex}) failed");
        return row.Metric;
    }

    /// <summary>
    /// LUID интерфейса по его индексу — нужен kill-switch'у (план, 3.3):
    /// условие FWPM_CONDITION_IP_LOCAL_INTERFACE в WFP матчится по LUID,
    /// не по индексу (который, как показал живой тест этапа 2, у TUN
    /// меняется при каждом запуске xray.exe).
    /// </summary>
    public static ulong GetInterfaceLuid(int interfaceIndex)
    {
        var err = IpHelper.ConvertInterfaceIndexToLuid((uint)interfaceIndex, out var luid);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, $"ConvertInterfaceIndexToLuid(ifIndex={interfaceIndex}) failed");
        return luid.Value;
    }
}
