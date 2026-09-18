namespace VlessTunnel.Native.Interop;

/// <summary>
/// Встроенные GUID/флаги WFP (fwpmtypes.h), нужные kill-switch'у (план,
/// 3.3). Значения СВЕРЕНЫ по официальной документации Microsoft и по
/// исходникам wireguard-windows (боевой, широко проверенный WFP-kill-switch)
/// — не взяты по памяти, в отличие от черновика в vm/rescue.ps1. Одно
/// расхождение с памятью уже поймано на этом же этапе: FWP_DATA_TYPE
/// начинается с FWP_EMPTY=0, а не FWP_UINT8=0 (см. WfpTypes.cs).
/// </summary>
internal static class WfpWellKnown
{
    // Слой ALE_AUTH_CONNECT — авторизация исходящего TCP/UDP-соединения,
    // до того как пакеты реально уйдут (план, 3.3: "запретить всё
    // остальное исходящее").
    public static readonly Guid LayerAleAuthConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerAleAuthConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");

    public static readonly Guid ConditionAleAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    public static readonly Guid ConditionIpLocalInterface = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4");
    public static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    public static readonly Guid ConditionIpRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    public static readonly Guid ConditionIpRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    public const uint ConditionFlagIsLoopback = 0x00000001;

    // "Постоянные" объекты (план, 3.3: "чтобы пережить падение службы") —
    // переживают остановку/перезапуск BFE (Base Filtering Engine), а не
    // только время жизни нашего дескриптора движка/сессии.
    public const uint FilterFlagPersistent = 0x00000001;
    public const uint SublayerFlagPersistent = 0x00000001;
    public const uint ProviderFlagPersistent = 0x00000001;
}
