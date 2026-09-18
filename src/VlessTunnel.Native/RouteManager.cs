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
}
