using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;
using Xunit;

namespace VlessTunnel.Native.Tests;

/// <summary>
/// Проверка бинарной совместимости управляемых структур с реальными
/// Win32-структурами (netioapi.h/ws2def.h), которые с ними обмениваются
/// CreateUnicastIpAddressEntry/CreateIpForwardEntry2/GetBestRoute2 (план,
/// 3.2). Размеры и смещения полей — из документации Microsoft для x64.
///
/// Это не паранойя: разработка этого файла нашла два реальных бага —
/// поле-заполнитель <c>ulong</c> вместо <c>char[8]</c> (в IN6_ADDR и в
/// SOCKADDR_IN.sin_zero) задавало выравнивание 8 байт вместо настоящих
/// 1-2, из-за чего .NET молча раздувал паддингом SOCKADDR_INET (28→32) и
/// смещал все поля после него в MIB_IPFORWARD_ROW2 на 8 байт — вызов
/// реального Win32 API с таким layout'ом писал бы мусор в чужие поля
/// структуры или падал.
/// </summary>
public sealed class StructLayoutTests
{
    [Fact]
    public void Sockaddr_in_is_16_bytes() => Assert.Equal(16, Marshal.SizeOf<SOCKADDR_IN>());

    [Fact]
    public void Sockaddr_in6_is_28_bytes() => Assert.Equal(28, Marshal.SizeOf<SOCKADDR_IN6>());

    [Fact]
    public void Sockaddr_inet_is_28_bytes() => Assert.Equal(28, Marshal.SizeOf<SOCKADDR_INET>());

    [Fact]
    public void Ip_address_prefix_is_32_bytes_with_prefix_at_0_and_length_at_28()
    {
        Assert.Equal(32, Marshal.SizeOf<IP_ADDRESS_PREFIX>());
        Assert.Equal(0, Marshal.OffsetOf<IP_ADDRESS_PREFIX>(nameof(IP_ADDRESS_PREFIX.Prefix)).ToInt32());
        Assert.Equal(28, Marshal.OffsetOf<IP_ADDRESS_PREFIX>(nameof(IP_ADDRESS_PREFIX.PrefixLength)).ToInt32());
    }

    [Fact]
    public void Mib_unicastipaddress_row_is_80_bytes_with_documented_offsets()
    {
        Assert.Equal(80, Marshal.SizeOf<MIB_UNICASTIPADDRESS_ROW>());
        Assert.Equal(0, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.Address)));
        Assert.Equal(32, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.InterfaceLuid)));
        Assert.Equal(40, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.InterfaceIndex)));
        Assert.Equal(60, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.OnLinkPrefixLength)));
        Assert.Equal(61, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.SkipAsSource)));
        Assert.Equal(72, Offset(nameof(MIB_UNICASTIPADDRESS_ROW.CreationTimeStamp)));

        static int Offset(string field) => Marshal.OffsetOf<MIB_UNICASTIPADDRESS_ROW>(field).ToInt32();
    }

    [Fact]
    public void Mib_ipforward_row2_is_104_bytes_with_documented_offsets()
    {
        Assert.Equal(104, Marshal.SizeOf<MIB_IPFORWARD_ROW2>());
        Assert.Equal(0, Offset(nameof(MIB_IPFORWARD_ROW2.InterfaceLuid)));
        Assert.Equal(8, Offset(nameof(MIB_IPFORWARD_ROW2.InterfaceIndex)));
        Assert.Equal(12, Offset(nameof(MIB_IPFORWARD_ROW2.DestinationPrefix)));
        Assert.Equal(44, Offset(nameof(MIB_IPFORWARD_ROW2.NextHop)));
        Assert.Equal(72, Offset(nameof(MIB_IPFORWARD_ROW2.SitePrefixLength)));
        Assert.Equal(92, Offset(nameof(MIB_IPFORWARD_ROW2.Loopback)));
        Assert.Equal(100, Offset(nameof(MIB_IPFORWARD_ROW2.Origin)));

        static int Offset(string field) => Marshal.OffsetOf<MIB_IPFORWARD_ROW2>(field).ToInt32();
    }

    [Fact]
    public void Net_luid_is_8_bytes() => Assert.Equal(8, Marshal.SizeOf<NET_LUID>());

    // Ревью п.22: MIB_IPINTERFACE_ROW — самая большая и рискованная из
    // P/Invoke структур в проекте (GetIpInterfaceEntry заполняет её по
    // ссылке нативным кодом), разметка целиком по официальной
    // документации Microsoft, не по памяти. Смещения ниже — не все поля
    // подряд, а опорные точки: начало/конец каждого блока однотипных
    // полей плюс Metric (единственное поле, которое реально читаем).
    [Fact]
    public void Mib_ipinterface_row_is_168_bytes_with_documented_offsets()
    {
        Assert.Equal(168, Marshal.SizeOf<MIB_IPINTERFACE_ROW>());
        Assert.Equal(0, Offset(nameof(MIB_IPINTERFACE_ROW.Family)));
        Assert.Equal(8, Offset(nameof(MIB_IPINTERFACE_ROW.InterfaceLuid)));
        Assert.Equal(16, Offset(nameof(MIB_IPINTERFACE_ROW.InterfaceIndex)));
        Assert.Equal(20, Offset(nameof(MIB_IPINTERFACE_ROW.MaxReassemblySize)));
        Assert.Equal(24, Offset(nameof(MIB_IPINTERFACE_ROW.InterfaceIdentifier)));
        Assert.Equal(40, Offset(nameof(MIB_IPINTERFACE_ROW.AdvertisingEnabled)));
        Assert.Equal(48, Offset(nameof(MIB_IPINTERFACE_ROW.AdvertiseDefaultRoute)));
        Assert.Equal(52, Offset(nameof(MIB_IPINTERFACE_ROW.RouterDiscoveryBehavior)));
        Assert.Equal(72, Offset(nameof(MIB_IPINTERFACE_ROW.LinkLocalAddressBehavior)));
        Assert.Equal(80, Offset(nameof(MIB_IPINTERFACE_ROW.ZoneIndices)));
        Assert.Equal(144, Offset(nameof(MIB_IPINTERFACE_ROW.SitePrefixLength)));
        Assert.Equal(148, Offset(nameof(MIB_IPINTERFACE_ROW.Metric)));
        Assert.Equal(152, Offset(nameof(MIB_IPINTERFACE_ROW.NlMtu)));
        Assert.Equal(156, Offset(nameof(MIB_IPINTERFACE_ROW.Connected)));
        Assert.Equal(160, Offset(nameof(MIB_IPINTERFACE_ROW.ReachableTime)));
        Assert.Equal(164, Offset(nameof(MIB_IPINTERFACE_ROW.TransmitOffload)));
        Assert.Equal(166, Offset(nameof(MIB_IPINTERFACE_ROW.DisableDefaultRoutes)));

        static int Offset(string field) => Marshal.OffsetOf<MIB_IPINTERFACE_ROW>(field).ToInt32();
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("fd00::1")]
    [InlineData("2001:db8::abcd")]
    public void Sockaddr_inet_roundtrips_ip_address(string ip)
    {
        var address = System.Net.IPAddress.Parse(ip);
        var sockaddr = SOCKADDR_INET.FromIpAddress(address);
        Assert.Equal(address, sockaddr.ToIpAddress());
    }
}
