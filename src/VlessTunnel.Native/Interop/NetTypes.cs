using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>
/// NET_LUID (netioapi.h) — 64-битный union, для наших целей достаточно
/// хранить его как непрозрачное значение, полученное из
/// ConvertInterfaceIndexToLuid, и передавать обратно как есть.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NET_LUID
{
    public ulong Value;
}

/// <summary>in_addr — 4 байта IPv4-адреса в сетевом порядке байт.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IN_ADDR
{
    public uint S_addr; // сетевой порядок байт (как из IPAddress.GetAddressBytes())
}

/// <summary>
/// in6_addr — 16 байт IPv6-адреса. Настоящий Win32 IN6_ADDR — union байтов
/// и USHORT'ов, то есть выравнивание всего 2 байта; собранный из двух
/// <c>ulong</c> (выравнивание 8) вариант раздувал бы SOCKADDR_IN6/
/// SOCKADDR_INET паддингом с 28 до 32 байт — поймано проверкой размеров
/// структур (Marshal.SizeOf) при разработке. Поэтому — 16 обычных полей
/// byte (выравнивание 1), без unsafe/fixed и без сюрпризов с паддингом.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IN6_ADDR
{
    public byte B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15;

    public static IN6_ADDR FromBytes(byte[] bytes)
    {
        if (bytes.Length != 16) throw new ArgumentException("IPv6 address must be 16 bytes", nameof(bytes));
        return new IN6_ADDR
        {
            B0 = bytes[0], B1 = bytes[1], B2 = bytes[2], B3 = bytes[3],
            B4 = bytes[4], B5 = bytes[5], B6 = bytes[6], B7 = bytes[7],
            B8 = bytes[8], B9 = bytes[9], B10 = bytes[10], B11 = bytes[11],
            B12 = bytes[12], B13 = bytes[13], B14 = bytes[14], B15 = bytes[15],
        };
    }

    public readonly byte[] ToBytes() =>
        [B0, B1, B2, B3, B4, B5, B6, B7, B8, B9, B10, B11, B12, B13, B14, B15];
}

/// <summary>
/// SOCKADDR_IN (ws2def.h) — используется внутри SOCKADDR_INET. Поля идут
/// в сетевом порядке байт, как ожидает Win32 API напрямую (без htons/htonl
/// в управляемом коде — конвертация на границе в NativeInterop.ToSockaddr).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SOCKADDR_IN
{
    public short sin_family; // AF_INET = 2
    public ushort sin_port;
    public IN_ADDR sin_addr;

    // sin_zero — заполнитель до размера SOCKADDR_IN6, реально char[8]
    // (выравнивание 1). Одно поле ulong тут (выравнивание 8) — та же
    // ошибка, что была найдена в IN6_ADDR: раздувает выравнивание
    // SOCKADDR_IN и, через union в SOCKADDR_INET, всех структур,
    // где SOCKADDR_INET вложен — поймано Marshal.OffsetOf на
    // MIB_IPFORWARD_ROW2 (DestinationPrefix уехал на offset 16 вместо 12).
#pragma warning disable CS0169 // поля только для паддинга/размера, не читаются
    private byte z0, z1, z2, z3, z4, z5, z6, z7;
#pragma warning restore CS0169
}

[StructLayout(LayoutKind.Sequential)]
internal struct SOCKADDR_IN6
{
    public short sin6_family; // AF_INET6 = 23
    public ushort sin6_port;
    public uint sin6_flowinfo;
    public IN6_ADDR sin6_addr;
    public uint sin6_scope_id;
}

/// <summary>
/// SOCKADDR_INET — union { SOCKADDR_IN Ipv4; SOCKADDR_IN6 Ipv6; short si_family; }.
/// В managed-коде смоделирован explicit-layout структурой на 28 байт
/// (размер SOCKADDR_IN6, самого крупного члена union'а).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct SOCKADDR_INET
{
    [FieldOffset(0)] public short si_family;
    [FieldOffset(0)] public SOCKADDR_IN Ipv4;
    [FieldOffset(0)] public SOCKADDR_IN6 Ipv6;

    public static SOCKADDR_INET FromIpAddress(IPAddress address)
    {
        var result = new SOCKADDR_INET();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            result.Ipv4.sin_family = (short)AddressFamily.InterNetwork;
            result.Ipv4.sin_addr.S_addr = BitConverter.ToUInt32(bytes, 0);
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            result.Ipv6.sin6_family = (short)AddressFamily.InterNetworkV6;
            result.Ipv6.sin6_addr = IN6_ADDR.FromBytes(bytes);
        }
        else
        {
            throw new ArgumentException($"Unsupported address family: {address.AddressFamily}", nameof(address));
        }
        return result;
    }

    public readonly IPAddress ToIpAddress()
    {
        if (si_family == (short)AddressFamily.InterNetwork)
        {
            return new IPAddress(BitConverter.GetBytes(Ipv4.sin_addr.S_addr));
        }
        if (si_family == (short)AddressFamily.InterNetworkV6)
        {
            return new IPAddress(Ipv6.sin6_addr.ToBytes());
        }
        throw new InvalidOperationException($"Unknown si_family: {si_family}");
    }
}
