using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>
/// Структуры wintrust.h для настоящей проверки Authenticode-подписи через
/// WinVerifyTrust (план 3.9 / ревью п.3 — X509Certificate.CreateFromSignedFile
/// извлекает сертификат из PE, но НЕ проверяет, что подпись действительна и
/// покрывает файл целиком). Поля и порядок сверены с документацией
/// Microsoft (learn.microsoft.com/.../ns-wintrust-wintrust_data,
/// .../ns-wintrust-wintrust_file_info) построчно, размеры/смещения
/// перепроверены тем же методом, что и для WFP (см. WfpTypes.cs) —
/// WinTrustStructLayoutTests.cs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_FILE_INFO
{
    public uint cbStruct;
    public nint pcwszFilePath; // LPCWSTR
    public nint hFile;         // HANDLE, необязателен
    public nint pgKnownSubject; // GUID*, необязателен
}

/// <summary>
/// Union dwUnionChoice/pFile..pDetachedSig размечен как один указатель
/// (nint pFile) — используется только WTD_CHOICE_FILE, остальные члены
/// union'а всегда лежат по тому же смещению 40 и нам не нужны.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WINTRUST_DATA
{
    public uint cbStruct;
    public nint pPolicyCallbackData;
    public nint pSIPClientData;
    public uint dwUIChoice;
    public uint fdwRevocationChecks;
    public uint dwUnionChoice;
    public nint pFile;
    public uint dwStateAction;
    public nint hWVTStateData;
    public nint pwszURLReference; // зарезервировано, всегда NULL
    public uint dwProvFlags;
    public uint dwUIContext;
    public nint pSignatureSettings; // Windows 7+KB3033929/8+ — можно NULL на более старых
}

internal static class WinTrustNative
{
    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;

    // WINTRUST_ACTION_GENERIC_VERIFY_V2 (wintrust.h) — та же политика
    // проверки, которую использует сам Windows для диалога Authenticode.
    public static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [DllImport("wintrust.dll", ExactSpelling = true)]
    public static extern int WinVerifyTrust(nint hwnd, ref Guid pgActionID, nint pWVTData);
}
