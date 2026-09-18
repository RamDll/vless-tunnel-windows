using System.ComponentModel;
using VlessTunnel.Native.Interop;

namespace VlessTunnel.Native;

/// <summary>
/// Слежение за сменой сети (план, 3.2: NotifyRouteChange2 /
/// NotifyIpInterfaceChange — смена Wi-Fi, переподключение кабеля, сон).
/// Событие <see cref="NetworkChanged"/> стреляет на КАЖДОЕ изменение
/// (может быть шумным — несколько раз подряд при одном физическом
/// событии), поэтому дебаунс — забота вызывающего кода (TunnelManager),
/// не этого класса. Колбэки вызываются на потоке ОС, не на потоке,
/// создавшем объект — обработчик события должен быть потокобезопасен.
/// </summary>
public sealed class RouteWatcher : IDisposable
{
    private const ushort AF_UNSPEC = 0;

    // Делегаты держим в полях, чтобы GC не собрал их, пока нативный код
    // хранит указатель на функцию (иначе — эпизодический краш через
    // случайное время после старта, самая коварная ошибка P/Invoke).
    private readonly RouteChangeCallback _routeCallback;
    private readonly InterfaceChangeCallback _interfaceCallback;

    private nint _routeHandle;
    private nint _interfaceHandle;
    private bool _disposed;

    public event Action? NetworkChanged;

    public RouteWatcher()
    {
        _routeCallback = (_, _, _) => NetworkChanged?.Invoke();
        _interfaceCallback = (_, _, _) => NetworkChanged?.Invoke();

        var err = IpHelper.NotifyRouteChange2(AF_UNSPEC, _routeCallback, 0, initialNotification: false, out _routeHandle);
        if (err != IpHelper.NO_ERROR)
            throw new Win32Exception((int)err, "NotifyRouteChange2 failed");

        err = IpHelper.NotifyIpInterfaceChange(AF_UNSPEC, _interfaceCallback, 0, initialNotification: false, out _interfaceHandle);
        if (err != IpHelper.NO_ERROR)
        {
            IpHelper.CancelMibChangeNotify2(_routeHandle);
            _routeHandle = 0;
            throw new Win32Exception((int)err, "NotifyIpInterfaceChange failed");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_routeHandle != 0) IpHelper.CancelMibChangeNotify2(_routeHandle);
        if (_interfaceHandle != 0) IpHelper.CancelMibChangeNotify2(_interfaceHandle);
    }
}
