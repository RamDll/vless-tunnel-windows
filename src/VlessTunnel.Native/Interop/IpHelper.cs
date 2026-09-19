using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>MIB_NOTIFICATION_TYPE (netioapi.h) — тип события в колбэках Notify*.</summary>
internal enum MibNotificationType
{
    ParameterNotification = 0,
    AddInstance = 1,
    DeleteInstance = 2,
    InitialNotification = 3,
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void RouteChangeCallback(nint callerContext, nint row, MibNotificationType notificationType);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void InterfaceChangeCallback(nint callerContext, nint row, MibNotificationType notificationType);

/// <summary>
/// Прямой P/Invoke к iphlpapi.dll (план, раздел 2: VlessTunnel.Native).
/// Управляемых обёрток для записи в таблицу маршрутов/адресов в .NET нет
/// (System.Net.NetworkInformation только читает), поэтому это неизбежно.
/// Классический DllImport, не source-generated LibraryImport — двум из
/// функций ниже нужно маршалить делегаты как указатели на функции, что
/// LibraryImport не поддерживает (SYSLIB1051).
/// Все функции возвращают Win32-код ошибки (0 = ERROR_SUCCESS).
/// </summary>
internal static class IpHelper
{
    internal const uint NO_ERROR = 0;
    // GetBestRoute2 с явным interfaceIndex (ревью п.22): "нет маршрута до
    // destination именно через этот интерфейс" — ожидаемый, не аварийный
    // исход перебора кандидатов, не просто "какая-то ошибка".
    internal const uint ERROR_NOT_FOUND = 1168;

    [DllImport("iphlpapi.dll")]
    internal static extern uint CreateUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll")]
    internal static extern uint DeleteUnicastIpAddressEntry(ref MIB_UNICASTIPADDRESS_ROW row);

    [DllImport("iphlpapi.dll")]
    internal static extern uint CreateIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll")]
    internal static extern uint DeleteIpForwardEntry2(ref MIB_IPFORWARD_ROW2 row);

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetBestRoute2(
        nint interfaceLuid,
        uint interfaceIndex,
        nint sourceAddress,
        ref SOCKADDR_INET destinationAddress,
        uint addressSortOptions,
        out MIB_IPFORWARD_ROW2 bestRoute,
        out SOCKADDR_INET bestSourceAddress);

    [DllImport("iphlpapi.dll")]
    internal static extern uint ConvertInterfaceIndexToLuid(uint interfaceIndex, out NET_LUID interfaceLuid);

    // Ревью п.22: метрика интерфейса — единственный ЧЕСТНО новый P/Invoke
    // (в отличие от GetBestRoute2 выше, у которого нужный параметр interfaceIndex
    // уже был объявлен, просто не использовался). Row передаётся по ссылке:
    // на входе достаточно Family+InterfaceIndex, остальное заполняет сама
    // функция.
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIpInterfaceEntry(ref MIB_IPINTERFACE_ROW row);

    [DllImport("iphlpapi.dll")]
    internal static extern uint NotifyRouteChange2(
        ushort addressFamily,
        RouteChangeCallback callback,
        nint callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out nint notificationHandle);

    [DllImport("iphlpapi.dll")]
    internal static extern uint NotifyIpInterfaceChange(
        ushort addressFamily,
        InterfaceChangeCallback callback,
        nint callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out nint notificationHandle);

    [DllImport("iphlpapi.dll")]
    internal static extern uint CancelMibChangeNotify2(nint notificationHandle);
}
