using System.ComponentModel;
using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;

namespace VlessTunnel.Native;

/// <summary>
/// WFP kill-switch (план, 3.3) — MVP-объём: разрешить xray.exe, loopback и
/// сам TUN-интерфейс на слое ALE_AUTH_CONNECT (V4/V6), запретить всё
/// остальное исходящее. Постоянные (persistent) provider/sublayer/filter —
/// переживают падение процесса и перезапуск BFE, снимаются только явным
/// <see cref="Uninstall"/>.
///
/// Сознательно НЕ входит в этот первый проход (план 3.3 упоминает, но это
/// отдельная работа поверх уже проверенного механизма): разрешение
/// приватных сетей при не-<c>--proxy-lan</c>, разрешение DHCP, запрет
/// DNS (порт 53) мимо TUN. Добавляются тем же способом — ещё парой
/// фильтров в <see cref="AddLayerFilters"/> — после того как этот базовый
/// набор проверен живым тестом на стенде.
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

    private const uint FwpErrorAlreadyExists = 0x80320009;
    private const uint FwpErrorNotFound = 0x80320012;

    public static void Install(string xrayExePath, int tunInterfaceIndex, Action<string>? trace = null)
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
                    AddLayerFilters(engine, WfpWellKnown.LayerAleAuthConnectV4, appIdBlob, tunLuid,
                        FilterAppV4, FilterLoopbackV4, FilterInterfaceV4, FilterBlockV4, Trace);
                    AddLayerFilters(engine, WfpWellKnown.LayerAleAuthConnectV6, appIdBlob, tunLuid,
                        FilterAppV6, FilterLoopbackV6, FilterInterfaceV6, FilterBlockV6, Trace);
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
            foreach (var key in new[] { FilterAppV4, FilterLoopbackV4, FilterInterfaceV4, FilterBlockV4, FilterAppV6, FilterLoopbackV6, FilterInterfaceV6, FilterBlockV6 })
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

    /// <summary>Неуправляемая память под один UINT64 — см. комментарий у AddLayerFilters.</summary>
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

    /// <summary>
    /// Четыре фильтра на слое (план, 3.3): разрешить xray.exe, разрешить
    /// loopback, разрешить сам TUN-интерфейс, запретить всё остальное.
    /// Permit-фильтры получают более высокий вес (10), чем catch-all block
    /// (0) — внутри одного sublayer выигрывает фильтр с бОльшим весом
    /// среди подошедших, поэтому явный порядок весов обязателен, а не
    /// "кто раньше создан".
    /// </summary>
    private static void AddLayerFilters(nint engine, Guid layer, nint appIdBlob, ulong tunLuid, Guid appKey, Guid loopbackKey, Guid interfaceKey, Guid blockKey, Action<string> trace)
    {
        AddFilter(engine, appKey, layer, "vless-tunnel: permit xray.exe", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionAleAppId, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.ByteBlobType, appIdBlob))], trace);

        AddFilter(engine, loopbackKey, layer, "vless-tunnel: permit loopback", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionFlags, FwpMatchType.FlagsAllSet, FWP_CONDITION_VALUE0.UInt32(WfpWellKnown.ConditionFlagIsLoopback))], trace);

        // FWP_CONDITION_VALUE0.uint64 — это "UINT64 *uint64" (документация:
        // "This value cannot be null"), а не встроенное значение — в отличие
        // от uint8/uint16/uint32, которые лежат в union'е инлайн. Класть туда
        // сам LUID напрямую — access violation внутри FwpmFilterAdd0 (WFP
        // пытается разыменовать LUID как адрес памяти): поймано живым тестом
        // на стенде (crash c0000005 в KERNELBASE.dll), не по документации —
        // это единственное место в WFP-структурах, где Marshal.SizeOf не
        // помог бы, потому что дело не в layout, а в семантике поля.
        using var tunLuidBuf = new NativeUInt64(tunLuid);
        AddFilter(engine, interfaceKey, layer, "vless-tunnel: permit TUN interface", FwpActionType.Permit, weight: 10,
            [Condition(WfpWellKnown.ConditionIpLocalInterface, FwpMatchType.Equal, FWP_CONDITION_VALUE0.AsPointer(FwpDataType.UInt64, tunLuidBuf.Pointer))], trace);

        AddFilter(engine, blockKey, layer, "vless-tunnel: block everything else", FwpActionType.Block, weight: 0, conditions: [], trace);
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
