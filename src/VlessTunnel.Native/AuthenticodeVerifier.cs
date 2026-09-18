using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;

namespace VlessTunnel.Native;

/// <summary>
/// Настоящая проверка Authenticode-подписи файла (план 3.9 / ревью п.3).
/// <c>X509Certificate.CreateFromSignedFile</c> (как раньше использовал
/// SelfUpdater) только ИЗВЛЕКАЕТ сертификат из PE — не проверяет, что
/// подпись действительна и реально покрывает файл целиком. Подделать блоб
/// с нужным отпечатком, но битой или отсутствующей подписью, тривиально —
/// единственной реальной привязкой оставался sha256 из того же релиза, то
/// есть якоря не было вовсе. WinVerifyTrust — то же самое, что делает сам
/// Windows перед показом диалога "издатель не может быть проверен".
/// </summary>
public static class AuthenticodeVerifier
{
    // INVALID_HANDLE_VALUE — рекомендация Microsoft для WTD_UI_NONE
    // (нет окна, чужого HWND нет и не нужно — вызывается из службы).
    private static readonly nint InvalidHandleValue = (nint)(-1);

    public static bool IsValidlySigned(string filePath)
    {
        var pathPtr = Marshal.StringToHGlobalUni(filePath);
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = pathPtr,
                hFile = 0,
                pgKnownSubject = 0,
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WinTrustNative.WTD_UI_NONE,
                // WTD_REVOKE_NONE — самоподписанный сертификат не имеет
                // точки распространения CRL/OCSP, проверять отзыв нечем;
                // пересмотреть, когда подпись перейдёт на SignPath (ревью
                // п.3, отдельный пункт про список отпечатков).
                fdwRevocationChecks = WinTrustNative.WTD_REVOKE_NONE,
                dwUnionChoice = WinTrustNative.WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WinTrustNative.WTD_STATEACTION_VERIFY,
            };
            Marshal.StructureToPtr(data, dataPtr, false);

            var action = WinTrustNative.ActionGenericVerifyV2;
            var result = WinTrustNative.WinVerifyTrust(InvalidHandleValue, ref action, dataPtr);

            // WTD_STATEACTION_CLOSE освобождает hWVTStateData, который
            // WinVerifyTrust записал обратно в структуру на предыдущем
            // вызове (VERIFY) — обязателен на каждый VERIFY по документации
            // WINTRUST_DATA, иначе течёт хэндл состояния провайдера доверия.
            // Дочитываем структуру ИЗ ПАМЯТИ (не из локальной копии) — там
            // актуальный hWVTStateData, выставленный самим WinVerifyTrust.
            var stateAfterVerify = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
            stateAfterVerify.dwStateAction = WinTrustNative.WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(stateAfterVerify, dataPtr, false);
            WinTrustNative.WinVerifyTrust(InvalidHandleValue, ref action, dataPtr);

            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(dataPtr);
        }
    }
}
