using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>
/// IP_ADDRESS_PREFIX (netioapi.h). Размер должен быть 32 байта на x64
/// (SOCKADDR_INET 28 байт + UINT8, выровнено до 4) — проверено
/// <c>NativeStructLayoutTests</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IP_ADDRESS_PREFIX
{
    public SOCKADDR_INET Prefix;
    public byte PrefixLength;
}

/// <summary>
/// MIB_UNICASTIPADDRESS_ROW (netioapi.h) — адрес TUN-адаптера (план, 3.2 п.5:
/// CreateUnicastIpAddressEntry). Ожидаемый размер на x64 — 80 байт.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MIB_UNICASTIPADDRESS_ROW
{
    public SOCKADDR_INET Address;
    public NET_LUID InterfaceLuid;
    public uint InterfaceIndex;
    public int PrefixOrigin;   // NL_PREFIX_ORIGIN
    public int SuffixOrigin;   // NL_SUFFIX_ORIGIN
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public byte OnLinkPrefixLength;
    public byte SkipAsSource;  // BOOLEAN (1 байт, не BOOL!)
    public int DadState;       // NL_DAD_STATE
    public uint ScopeId;       // SCOPE_ID union
    public long CreationTimeStamp;

    public static MIB_UNICASTIPADDRESS_ROW ForAddress(System.Net.IPAddress address, uint interfaceIndex, byte onLinkPrefixLength) => new()
    {
        Address = SOCKADDR_INET.FromIpAddress(address),
        InterfaceIndex = interfaceIndex,
        InterfaceLuid = default,
        PrefixOrigin = 1,  // IpPrefixOriginManual
        SuffixOrigin = 1,  // IpSuffixOriginManual
        ValidLifetime = 0xFFFFFFFF,
        PreferredLifetime = 0xFFFFFFFF,
        OnLinkPrefixLength = onLinkPrefixLength,
        SkipAsSource = 0,
        DadState = 4,      // IpDadStatePreferred — сразу пригоден к использованию, без DAD (наш адрес не публикуется)
        ScopeId = 0,
        CreationTimeStamp = 0,
    };
}

/// <summary>
/// MIB_IPFORWARD_ROW2 (netioapi.h) — маршрут (план, 3.2 п.6:
/// CreateIpForwardEntry2). Ожидаемый размер на x64 — 104 байта.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MIB_IPFORWARD_ROW2
{
    public NET_LUID InterfaceLuid;
    public uint InterfaceIndex;
    public IP_ADDRESS_PREFIX DestinationPrefix;
    public SOCKADDR_INET NextHop;
    public byte SitePrefixLength;
    public uint ValidLifetime;
    public uint PreferredLifetime;
    public uint Metric;
    public int Protocol;       // NL_ROUTE_PROTOCOL
    public byte Loopback;      // BOOLEAN
    public byte AutoconfigureAddress; // BOOLEAN
    public byte Publish;       // BOOLEAN
    public byte Immortal;      // BOOLEAN
    public uint Age;
    public int Origin;         // NL_ROUTE_ORIGIN

    /// <summary>
    /// Свой NL_ROUTE_PROTOCOL — метка "этот маршрут создан нами" (план,
    /// 3.2: маршруты снимаются "по метке, не по памяти процесса"). Диапазон
    /// 10000–16383 зарезервирован под частные протоколы (как это делает,
    /// например, RRAS), поэтому будущий `doctor`/rescue сможет найти и
    /// снять наши маршруты через GetIpForwardTable2 даже после падения
    /// службы, не полагаясь на список из живого процесса.
    /// </summary>
    public const int OwnRouteProtocol = 11000;

    public static MIB_IPFORWARD_ROW2 ForRoute(System.Net.IPAddress destinationNetwork, byte prefixLength, System.Net.IPAddress? nextHop, uint interfaceIndex, uint metric, int protocol = OwnRouteProtocol) => new()
    {
        InterfaceIndex = interfaceIndex,
        DestinationPrefix = new IP_ADDRESS_PREFIX
        {
            Prefix = SOCKADDR_INET.FromIpAddress(destinationNetwork),
            PrefixLength = prefixLength,
        },
        NextHop = SOCKADDR_INET.FromIpAddress(nextHop ?? (destinationNetwork.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? System.Net.IPAddress.Any : System.Net.IPAddress.IPv6Any)),
        SitePrefixLength = 0,
        ValidLifetime = 0xFFFFFFFF,
        PreferredLifetime = 0xFFFFFFFF,
        Metric = metric,
        Protocol = protocol,
        Loopback = 0,
        AutoconfigureAddress = 0,
        Publish = 0,
        Immortal = 0,
        Age = 0,
        Origin = 0,     // NlroManual
    };
}

/// <summary>
/// MIB_IPINTERFACE_ROW (netioapi.h) — нужен только <see cref="Metric"/>
/// (ревью п.22: интерфейсная составляющая эффективной метрики маршрута,
/// см. GetIpInterfaceEntry/RouteManager.GetInterfaceMetric). Разметка
/// взята дословно из официальной документации Microsoft (все поля по
/// порядку, включая NL_INTERFACE_OFFLOAD_ROD — битовое поле над BOOLEAN,
/// то есть 1 байт), не по памяти — самая большая и рискованная из P/Invoke
/// структур в этом проекте, ошибка в разметке при вызове GetIpInterfaceEntry
/// (структура передаётся по ссылке и заполняется нативным кодом) могла бы
/// побить память за её пределами. Ожидаемый размер на x64 — 168 байт,
/// проверено <c>NativeStructLayoutTests</c> и живым тестом (возвращённый
/// Metric сверен с Get-NetIPInterface на стенде).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MIB_IPINTERFACE_ROW
{
    public short Family;                              // ADDRESS_FAMILY, offset 0 (6 байт паддинга до NET_LUID)
    public NET_LUID InterfaceLuid;                     // offset 8
    public uint InterfaceIndex;                        // offset 16 (NET_IFINDEX = ULONG)
    public uint MaxReassemblySize;                     // offset 20
    public ulong InterfaceIdentifier;                  // offset 24
    public uint MinRouterAdvertisementInterval;        // offset 32
    public uint MaxRouterAdvertisementInterval;        // offset 36
    public byte AdvertisingEnabled;                    // offset 40 (BOOLEAN)
    public byte ForwardingEnabled;                     // offset 41
    public byte WeakHostSend;                          // offset 42
    public byte WeakHostReceive;                       // offset 43
    public byte UseAutomaticMetric;                    // offset 44
    public byte UseNeighborUnreachabilityDetection;    // offset 45
    public byte ManagedAddressConfigurationSupported;  // offset 46
    public byte OtherStatefulConfigurationSupported;   // offset 47
    public byte AdvertiseDefaultRoute;                 // offset 48 (3 байта паддинга до enum)
    public int RouterDiscoveryBehavior;                // offset 52, NL_ROUTER_DISCOVERY_BEHAVIOR
    public uint DadTransmits;                          // offset 56
    public uint BaseReachableTime;                     // offset 60
    public uint RetransmitTime;                        // offset 64
    public uint PathMtuDiscoveryTimeout;               // offset 68
    public int LinkLocalAddressBehavior;               // offset 72, NL_LINK_LOCAL_ADDRESS_BEHAVIOR
    public uint LinkLocalAddressTimeout;                // offset 76
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public uint[] ZoneIndices;                          // offset 80, ScopeLevelCount=16 -> 64 байта
    public uint SitePrefixLength;                       // offset 144
    public uint Metric;                                 // offset 148 — единственное, что нам реально нужно
    public uint NlMtu;                                  // offset 152
    public byte Connected;                              // offset 156
    public byte SupportsWakeUpPatterns;                 // offset 157
    public byte SupportsNeighborDiscovery;              // offset 158
    public byte SupportsRouterDiscovery;                // offset 159
    public uint ReachableTime;                          // offset 160
    public byte TransmitOffload;                        // offset 164, NL_INTERFACE_OFFLOAD_ROD (битовые флаги, 1 байт)
    public byte ReceiveOffload;                         // offset 165
    public byte DisableDefaultRoutes;                   // offset 166 (паддинг до 168 в конце структуры)

    public static MIB_IPINTERFACE_ROW ForQuery(uint interfaceIndex) => new()
    {
        Family = 2, // AF_INET — GetIpInterfaceEntry требует явно проставленный Family на входе
        InterfaceIndex = interfaceIndex,
        ZoneIndices = new uint[16],
    };
}
