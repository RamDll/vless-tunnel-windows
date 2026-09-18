using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>
/// Прямой P/Invoke к fwpuclnt.dll (fwpmu.h) — создание/удаление
/// provider/sublayer/filter для kill-switch'а (план, 3.3). Удаление по GUID
/// (FwpmFilterDeleteByKey0 и т.д.) уже было написано заранее в
/// vm/rescue.ps1 (Add-Type) для аварийного снятия — здесь тот же набор,
/// плюс функции создания, которых rescue.ps1 не касался.
/// Все функции возвращают Win32-код ошибки (0 = ERROR_SUCCESS).
/// </summary>
internal static class WfpEngine
{
    internal const uint NO_ERROR = 0;
    private const uint RPC_C_AUTHN_WINNT = 10;

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
        uint authnService, nint authIdentity, nint session, out nint engineHandle);

    internal static uint FwpmEngineOpen0(out nint engineHandle) =>
        FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, 0, 0, out engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmEngineClose0(nint engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionBegin0(nint engineHandle, uint flags);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionCommit0(nint engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmTransactionAbort0(nint engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmProviderAdd0(nint engineHandle, ref FWPM_PROVIDER0 provider, nint sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmProviderDeleteByKey0(nint engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmSubLayerAdd0(nint engineHandle, ref FWPM_SUBLAYER0 subLayer, nint sd);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmSubLayerDeleteByKey0(nint engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterAdd0(nint engineHandle, ref FWPM_FILTER0 filter, nint sd, out ulong id);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterDeleteByKey0(nint engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterCreateEnumHandle0(nint engineHandle, nint enumTemplate, out nint enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterEnum0(nint engineHandle, nint enumHandle, uint numEntriesRequested, out nint entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern uint FwpmFilterDestroyEnumHandle0(nint engineHandle, nint enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern uint FwpmGetAppIdFromFileName0(string fileName, out nint appId /* FWP_BYTE_BLOB* */);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    internal static extern void FwpmFreeMemory0(ref nint p);
}
