using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;

namespace VlessTunnel.Native;

/// <summary>
/// WFP kill-switch (план, 3.3): на слое ALE_AUTH_CONNECT (V4/V6) — разрешить
/// xray.exe, loopback, сам TUN-интерфейс, приватные сети (если не
/// <c>excludeLan:false</c>) и DHCP; запретить порт 53 (DNS) мимо TUN;
/// запретить всё остальное исходящее. Постоянные (persistent)
/// provider/sublayer/filter — переживают падение процесса и перезапуск
/// BFE, снимаются только явным <see cref="Uninstall"/>.
///
/// Порядок весов внутри sublayer (выигрывает подошедший фильтр с бОльшим
/// весом, не "кто раньше создан"): 10 — xray.exe/loopback/TUN-интерфейс;
/// 8 — запрет DNS мимо TUN (обязан перебивать разрешение приватных сетей
/// ниже, иначе DNS-сервер в LAN стал бы дырой); 5 — приватные сети/DHCP;
/// 0 — catch-all запрет.
///
/// Ещё не сделано (план 3.3 упоминает, но не в этом проходе): команда
/// <c>doctor</c> (аварийный поиск/снятие зависших фильтров — черновик
/// уже есть в vm/rescue.ps1, но не как часть публичного API этого класса).
/// </summary>
public static class KillSwitch
{
    public static readonly Guid ProviderGuid = new("64291c58-52b0-4fae-8d47-8af2cbec87c3");
    public static readonly Guid SublayerGuid = new("f5e23d33-098e-444a-a312-7b190a764453");

    // Фиксированные ключи фильтров (план, 3.2/3.3: маршруты и фильтры
    // "снимаются по метке, не по памяти процесса") — Uninstall() удаляет
    // ИМЕННО их по GUID, без перечисления, поэтому работает даже из
    // другого процесса и после падения службы (тот же принцип, что уже
    // применён к маршрутам через OwnRouteProtocol в MibTypes.cs).
    private static readonly Guid FilterAppV4 = new("0a82bdba-be0c-4304-824e-bbee580f985f");
    private static readonly Guid FilterLoopbackV4 = new("6194b57e-a480-4e54-92b8-511e1a11ae4b");
    private static readonly Guid FilterInterfaceV4 = new("cb937cbc-08e7-4a34-9435-5b270b59216f");
    private static readonly Guid FilterBlockV4 = new("3c2fc1c1-8ff2-4f2f-bb04-b21c728c69ec");
    private static readonly Guid FilterAppV6 = new("a2d3633f-b77c-449b-bf51-9c4d3932dbc0");
    private static readonly Guid FilterLoopbackV6 = new("13e6ab96-6d64-4911-8fa5-caf72c7e41e9");
    private static readonly Guid FilterInterfaceV6 = new("c0805eb2-c2eb-4d49-a83c-d153e31082ed");
    private static readonly Guid FilterBlockV6 = new("84b7f87a-4244-406d-a443-aa3daaca793a");
    private static readonly Guid FilterDnsBlockV4 = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9b01");
    private static readonly Guid FilterDnsBlockV6 = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9b02");
    private static readonly Guid FilterDhcpV4 = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9b03");
    private static readonly Guid FilterDhcpV6 = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9b04");

    // Тот же список приватных сетей, что в Core/ConfigBuilder.cs (общий
    // источник истины с Linux-эталоном) — здесь отдельная копия, потому
    // что VlessTunnel.Native сознательно не зависит от Core.
    private static readonly (string Network, byte Prefix)[] Private4 =
    [
        ("0.0.0.0", 8), ("10.0.0.0", 8), ("100.64.0.0", 10), ("127.0.0.0", 8), ("169.254.0.0", 16),
        ("172.16.0.0", 12), ("192.0.0.0", 24), ("192.0.2.0", 24), ("192.168.0.0", 16), ("198.18.0.0", 15),
        ("198.51.100.0", 24), ("203.0.113.0", 24), ("224.0.0.0", 4), ("240.0.0.0", 4),
    ];

    private static readonly (string Network, byte Prefix)[] Private6 =
        [("::1", 128), ("fc00::", 7), ("fe80::", 10), ("ff00::", 8)];

    // Ключи для приватных сетей выводятся из базового GUID подстановкой
    // последнего байта индексом — не нужно хранить/вручную придумывать
    // 18 отдельных констант, а Uninstall() всё равно может их
    // детерминированно восстановить по тем же спискам Private4/Private6.
    private static readonly Guid PrivateNetV4Base = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9c00");
    private static readonly Guid PrivateNetV6Base = new("f21b6b1a-2e5b-4a3c-9c2a-6a8f1e6a9d00");

    private static Guid DerivedGuid(Guid baseGuid, int index)
    {
        var bytes = baseGuid.ToByteArray();
        bytes[15] = checked((byte)index);
        return new Guid(bytes);
    }

    private const uint FwpErrorAlreadyExists = 0x80320009;
    private const uint FwpErrorNotFound = 0x80320012;
    private const ushort DhcpServerPortV4 = 67;
    private const ushort DhcpServerPortV6 = 547;
    private const ushort DnsPort = 53;

    public static void Install(string xrayExePath, int tunInterfaceIndex, bool excludeLan = true, Action<string>? trace = null)
    {
        void Trace(string s) { trace?.Invoke(s); }

        Trace("FwpmEngineOpen0...");
        var err = WfpEngine.FwpmEngineOpen0(out var engine);
        if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmEngineOpen0 failed");
        Trace("FwpmEngineOpen0 ok");
        try
        {
            Trace("FwpmTransactionBegin0...");
            err = WfpEngine.FwpmTransactionBegin0(engine, 0);
            if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmTransactionBegin0 failed");
            Trace("FwpmTransactionBegin0 ok");
            try
            {
                AddProviderAndSublayer(engine, Trace);

                Trace("FwpmGetAppIdFromFileName0...");
                err = WfpEngine.FwpmGetAppIdFromFileName0(xrayExePath, out var appIdBlob);
                if (err != WfpEngine.NO_ERROR)
                    throw new Win32Exception((int)err, $"FwpmGetAppIdFromFileName0({xrayExePath}) failed");
                Trace($"FwpmGetAppIdFromFileName0 ok, blob=0x{appIdBlob:X}");
                try
                {
                    var tunLuid = RouteManager.GetInterfaceLuid(tunInterfaceIndex);
                    Trace($"tunLuid={tunLuid}");

                    using var tunLuidBuf = new NativeUInt64(tunLuid);

                    AddCommonFilters(engine, WfpWellKnown.LayerAleAuthConnectV4, appIdBlob, tunLuidBuf.Pointer,
                        FilterAppV4, FilterLoopbackV4, FilterInterfaceV4, FilterBlockV4, Trace);
                    AddDnsBlock(engine, WfpWellKnown.LayerAleAuthConnectV4, FilterDnsBlockV4, Trace);
                    AddDhcpPermit(engine, WfpWellKnown.LayerAleAuthConnectV4, FilterDhcpV4, DhcpServerPortV4, Trace);
                    if (excludeLan) AddPrivateNetPermits(engine, WfpWellKnown.LayerAleAuthConnectV4, isV6: false, Trace);

                    AddCommonFilters(engine, WfpWellKnown.LayerAleAuthConnectV6, appIdBlob, tunLuidBuf.Pointer,
                        FilterAppV6, FilterLoopbackV6, FilterInterfaceV6, FilterBlockV6, Trace);
                    AddDnsBlock(engine, WfpWellKnown.LayerAleAuthConnectV6, FilterDnsBlockV6, Trace);
                    AddDhcpPermit(engine, WfpWellKnown.LayerAleAuthConnectV6, FilterDhcpV6, DhcpServerPortV6, Trace);
                    if (excludeLan) AddPrivateNetPermits(engine, WfpWellKnown.LayerAleAuthConnectV6, isV6: true, Trace);
                }
                finally
                {
                    Trace("FwpmFreeMemory0...");
                    WfpEngine.FwpmFreeMemory0(ref appIdBlob);
                    Trace("FwpmFreeMemory0 ok");
                }

                Trace("FwpmTransactionCommit0...");
                err = WfpEngine.FwpmTransactionCommit0(engine);
                if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmTransactionCommit0 failed");
                Trace("FwpmTransactionCommit0 ok");
            }
            catch
            {
                WfpEngine.FwpmTransactionAbort0(engine);
                throw;
            }
        }
        finally
        {
            WfpEngine.FwpmEngineClose0(engine);
        }
    }

    /// <summary>
    /// Снятие по фиксированным GUID (best-effort — как rescue.ps1, но здесь
    /// это штатный путь, не только аварийный). Работает и если что-то из
    /// перечисленного уже отсутствует (NOT_FOUND — не ошибка).
    /// </summary>
    public static void Uninstall()
    {
        var err = WfpEngine.FwpmEngineOpen0(out var engine);
        if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmEngineOpen0 failed");
        try
        {
            var keys = new List<Guid>
            {
                FilterAppV4, FilterLoopbackV4, FilterInterfaceV4, FilterBlockV4,
                FilterAppV6, FilterLoopbackV6, FilterInterfaceV6, FilterBlockV6,
                FilterDnsBlockV4, FilterDnsBlockV6, FilterDhcpV4, FilterDhcpV6,
            };
            for (var i = 0; i < Private4.Length; i++) keys.Add(DerivedGuid(PrivateNetV4Base, i));
            for (var i = 0; i < Private6.Length; i++) keys.Add(DerivedGuid(PrivateNetV6Base, i));

            foreach (var key in keys)
            {
                var k = key;
                var e = WfpEngine.FwpmFilterDeleteByKey0(engine, ref k);
                if (e != WfpEngine.NO_ERROR && e != FwpErrorNotFound)
                    throw new Win32Exception((int)e, $"FwpmFilterDeleteByKey0({key}) failed");
            }

            var sl = SublayerGuid;
            var slErr = WfpEngine.FwpmSubLayerDeleteByKey0(engine, ref sl);
            if (slErr != WfpEngine.NO_ERROR && slErr != FwpErrorNotFound)
                throw new Win32Exception((int)slErr, "FwpmSubLayerDeleteByKey0 failed");

            var pv = ProviderGuid;
            var pvErr = WfpEngine.FwpmProviderDeleteByKey0(engine, ref pv);
            if (pvErr != WfpEngine.NO_ERROR && pvErr != FwpErrorNotFound)
                throw new Win32Exception((int)pvErr, "FwpmProviderDeleteByKey0 failed");
        }
        finally
        {
            WfpEngine.FwpmEngineClose0(engine);
        }
    }

    /// <summary>
    /// Аварийное снятие (план, 3.3/3.9: "doctor снимает фильтры") —
    /// в отличие от <see cref="Uninstall"/> не полагается на список
    /// известных фиксированных GUID, а перечисляет ВСЕ фильтры в системе
    /// и снимает те, чей provider/sublayer совпадает с нашим. Находит и
    /// снимает фильтры, даже если список GUID в будущей версии кода
    /// разойдётся с тем, что реально стоит в системе (например, после
    /// обновления с более старой версии). Возвращает число снятых
    /// фильтров. Черновик той же идеи (но через ручное чтение смещений
    /// байт вместо Marshal.PtrToStructure) уже был написан заранее в
    /// vm/rescue.ps1 для случая, когда файлов программы уже нет вовсе —
    /// rescue.ps1 должен остаться самостоятельным и не звать этот метод.
    /// </summary>
    public static int Doctor(Action<string>? trace = null)
    {
        void Trace(string s) { trace?.Invoke(s); }

        var err = WfpEngine.FwpmEngineOpen0(out var engine);
        if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmEngineOpen0 failed");
        try
        {
            var removed = 0;
            Trace("Doctor: FwpmFilterCreateEnumHandle0...");
            err = WfpEngine.FwpmFilterCreateEnumHandle0(engine, 0, out var enumHandle);
            if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmFilterCreateEnumHandle0 failed");
            try
            {
                err = WfpEngine.FwpmFilterEnum0(engine, enumHandle, 4096, out var entries, out var returned);
                if (err != WfpEngine.NO_ERROR) throw new Win32Exception((int)err, "FwpmFilterEnum0 failed");
                Trace($"Doctor: enumerated {returned} filters total");
                try
                {
                    var ptrs = new nint[returned];
                    if (returned > 0) Marshal.Copy(entries, ptrs, 0, (int)returned);
                    foreach (var p in ptrs)
                    {
                        var filter = Marshal.PtrToStructure<FWPM_FILTER0>(p);
                        var hasProvider = filter.providerKey != 0;
                        var providerKey = hasProvider ? Marshal.PtrToStructure<Guid>(filter.providerKey) : Guid.Empty;
                        if ((hasProvider && providerKey == ProviderGuid) || filter.subLayerKey == SublayerGuid)
                        {
                            var key = filter.filterKey;
                            var d = WfpEngine.FwpmFilterDeleteByKey0(engine, ref key);
                            Trace($"Doctor: filter {key} delete=0x{d:X}");
                            if (d == WfpEngine.NO_ERROR) removed++;
                        }
                    }
                }
                finally
                {
                    WfpEngine.FwpmFreeMemory0(ref entries);
                }
            }
            finally
            {
                WfpEngine.FwpmFilterDestroyEnumHandle0(engine, enumHandle);
            }

            var sl = SublayerGuid;
            var slErr = WfpEngine.FwpmSubLayerDeleteByKey0(engine, ref sl);
            Trace($"Doctor: sublayer delete=0x{slErr:X}");

            var pv = ProviderGuid;
            var pvErr = WfpEngine.FwpmProviderDeleteByKey0(engine, ref pv);
            Trace($"Doctor: provider delete=0x{pvErr:X}");

            return removed;
        }
        finally
        {
            WfpEngine.FwpmEngineClose0(engine);
        }
    }

    private static void AddProviderAndSublayer(nint engine, Action<string> trace)
    {
        trace("AddProviderAndSublayer: provider...");
        using (var name = new NativeUnicodeString("vless-tunnel kill-switch"))
        {
            var provider = new FWPM_PROVIDER0
            {
                providerKey = ProviderGuid,
                displayData = new FWPM_DISPLAY_DATA0 { name = name.Pointer },
                flags = WfpWellKnown.ProviderFlagPersistent,
            };
            var err = WfpEngine.FwpmProviderAdd0(engine, ref provider, 0);
            if (err != WfpEngine.NO_ERROR && err != FwpErrorAlreadyExists)
                throw new Win32Exception((int)err, "FwpmProviderAdd0 failed");
        }
        trace("AddProviderAndSublayer: provider ok, sublayer...");

        using (var name = new NativeUnicodeString("vless-tunnel kill-switch"))
        {
            var sublayer = new FWPM_SUBLAYER0
            {
                subLayerKey = SublayerGuid,
                displayData = new FWPM_DISPLAY_DATA0 { name = name.Pointer },
                flags = WfpWellKnown.SublayerFlagPersistent,
                weight = 0xFFFF,
            };
            var err = WfpEngine.FwpmSubLayerAdd0(engine, ref sublayer, 0);
            if (err != WfpEngine.NO_ERROR && err != FwpErrorAlreadyExists)
                throw new Win32Exception((int)err, "FwpmSubLayerAdd0 failed");
        }
        trace("AddProviderAndSublayer: sublayer ok");
    }

    /// <summary>
    /// Строка в неуправляемой памяти для полей вроде FWPM_DISPLAY_DATA0.name
    /// (LPWSTR) — FwpmXxxAdd0 копирует содержимое синхронно внутри вызова
    /// (в отличие от Enum0/GetAppIdFromFileName0, которые сами выделяют
    /// память через FwpmFreeMemory0), поэтому строку можно освобождать
    /// сразу после Add0, в отличие от appIdBlob.
    /// </summary>
    private readonly struct NativeUnicodeString : IDisposable
    {
        public nint Pointer { get; }
        public NativeUnicodeString(string value) => Pointer = Marshal.StringToHGlobalUni(value);
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    /// <summary>
    /// Неуправляемая память под один UINT64 — FWP_CONDITION_VALUE0.uint64
    /// это "UINT64 *uint64" (документация Microsoft: "This value cannot be
    /// null"), а не встроенное значение, как uint8/uint16/uint32 в том же
    /// union'е. Класть туда сам LUID напрямую — access violation внутри
    /// FwpmFilterAdd0 (WFP пытается разыменовать LUID как адрес памяти):
    /// поймано живым тестом на стенде (crash c0000005 в KERNELBASE.dll),
    /// не по документации — единственное место в WFP-структурах, где
    /// Marshal.SizeOf не помог бы, потому что дело не в layout, а в
    /// семантике поля.
    /// </summary>
    private readonly struct NativeUInt64 : IDisposable
    {
        public nint Pointer { get; }
        public NativeUInt64(ulong value)
        {
            Pointer = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(Pointer, unchecked((long)value));
        }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    /// <summary>Разрешить xray.exe, loopback, сам TUN-интерфейс; запретить всё остальное (вес 10/10/10/0).</summary>
    private static void AddCommonFilters(nint engine, Guid layer, nint appIdBlob, nint tunLuidPtr, Guid appKey, Guid loopbackKey, Guid interfaceKey, Guid blockKey, Action<string> trace)
    {
        AddFilter(engine, appKey, layer, "vless-tunnel: permit xray.exe", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionAleAppId, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.ByteBlobType, appIdBlob))], trace);

        AddFilter(engine, loopbackKey, layer, "vless-tunnel: permit loopback", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionFlags, FwpMatchType.FlagsAllSet, FWP_CONDITION_VALUE0.UInt32(WfpWellKnown.ConditionFlagIsLoopback))], trace);

        AddFilter(engine, interfaceKey, layer, "vless-tunnel: permit TUN interface", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionIpLocalInterface, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.UInt64, tunLuidPtr))], trace);

        AddFilter(engine, blockKey, layer, "vless-tunnel: block everything else", FwpActionType.Block, weight: 0, conditions: [], trace);
    }

    /// <summary>
    /// Запрет порта 53 (план, 3.3: "Windows параллельно шлёт DNS во все
    /// интерфейсы") — вес 8, обязан перебивать permit приватных сетей
    /// (вес 5) ниже, иначе DNS-сервер в LAN стал бы утечкой; проигрывает
    /// permit'ам xray.exe/TUN-интерфейса (вес 10), так что DNS через
    /// туннель по-прежнему разрешён.
    /// </summary>
    private static void AddDnsBlock(nint engine, Guid layer, Guid key, Action<string> trace) =>
        AddFilter(engine, key, layer, "vless-tunnel: block DNS outside TUN", FwpActionType.Block, weight: 8,
            [Condition(WfpWellKnown.ConditionIpRemotePort, FwpMatchType.Equal, FWP_CONDITION_VALUE0.UInt16(DnsPort))], trace);

    private static void AddDhcpPermit(nint engine, Guid layer, Guid key, ushort port, Action<string> trace) =>
        AddFilter(engine, key, layer, "vless-tunnel: permit DHCP", FwpActionType.Permit, weight: 5,
            [Condition(WfpWellKnown.ConditionIpRemotePort, FwpMatchType.Equal, FWP_CONDITION_VALUE0.UInt16(port))], trace);

    /// <summary>
    /// Приватные сети остаются на физическом интерфейсе при не-<c>--proxy-lan</c>
    /// (тот же список, что в Core/ConfigBuilder.cs) — иначе такой трафик
    /// упёрся бы в catch-all block вместе со всем остальным.
    /// </summary>
    private static void AddPrivateNetPermits(nint engine, Guid layer, bool isV6, Action<string> trace)
    {
        var list = isV6 ? Private6 : Private4;
        var baseGuid = isV6 ? PrivateNetV6Base : PrivateNetV4Base;
        for (var i = 0; i < list.Length; i++)
        {
            var (network, prefix) = list[i];
            var address = IPAddress.Parse(network);
            if (isV6)
            {
                var v6 = FWP_V6_ADDR_AND_MASK.FromCidr(address, prefix);
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_V6_ADDR_AND_MASK>());
                try
                {
                    Marshal.StructureToPtr(v6, ptr, false);
                    AddFilter(engine, DerivedGuid(baseGuid, i), layer, $"vless-tunnel: permit private net {network}/{prefix}", FwpActionType.Permit, weight: 5,
                        [Condition(WfpWellKnown.ConditionIpRemoteAddress, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.V6AddrMask, ptr))], trace);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            else
            {
                var v4 = FWP_V4_ADDR_AND_MASK.FromCidr(address, prefix);
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FWP_V4_ADDR_AND_MASK>());
                try
                {
                    Marshal.StructureToPtr(v4, ptr, false);
                    AddFilter(engine, DerivedGuid(baseGuid, i), layer, $"vless-tunnel: permit private net {network}/{prefix}", FwpActionType.Permit, weight: 5,
                        [Condition(WfpWellKnown.ConditionIpRemoteAddress, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.V4AddrMask, ptr))], trace);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }
    }

    private static FWPM_FILTER_CONDITION0 Condition(Guid fieldKey, uint matchType, FWP_CONDITION_VALUE0 value) =>
        new() { fieldKey = fieldKey, matchType = matchType, conditionValue = value };

    private static void AddFilter(nint engine, Guid filterKey, Guid layer, string name, uint action, byte weight, FWPM_FILTER_CONDITION0[] conditions, Action<string> trace)
    {
        trace($"AddFilter \"{name}\" start, conditions={conditions.Length}");
        nint conditionsPtr = 0;
        using var displayName = new NativeUnicodeString(name);
        try
        {
            if (conditions.Length > 0)
            {
                var conditionSize = Marshal.SizeOf<FWPM_FILTER_CONDITION0>();
                conditionsPtr = Marshal.AllocHGlobal(conditionSize * conditions.Length);
                for (var i = 0; i < conditions.Length; i++)
                    Marshal.StructureToPtr(conditions[i], conditionsPtr + i * conditionSize, false);
            }

            var filter = new FWPM_FILTER0
            {
                filterKey = filterKey,
                displayData = new FWPM_DISPLAY_DATA0 { name = displayName.Pointer },
                flags = WfpWellKnown.FilterFlagPersistent,
                layerKey = layer,
                subLayerKey = SublayerGuid,
                weight = FWP_CONDITION_VALUE0.UInt8(weight),
                numFilterConditions = (uint)conditions.Length,
                filterCondition = conditionsPtr,
                action = new FWPM_ACTION0 { type = action },
            };

            trace($"AddFilter \"{name}\": calling FwpmFilterAdd0...");
            var err = WfpEngine.FwpmFilterAdd0(engine, ref filter, 0, out _);
            trace($"AddFilter \"{name}\": FwpmFilterAdd0 returned 0x{err:X}");
            if (err != WfpEngine.NO_ERROR && err != FwpErrorAlreadyExists)
                throw new Win32Exception((int)err, $"FwpmFilterAdd0(\"{name}\") failed");
        }
        finally
        {
            if (conditionsPtr != 0) Marshal.FreeHGlobal(conditionsPtr);
        }
        trace($"AddFilter \"{name}\" done");
    }
}
