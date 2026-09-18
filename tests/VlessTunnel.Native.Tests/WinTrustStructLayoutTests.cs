using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;
using Xunit;

namespace VlessTunnel.Native.Tests;

/// <summary>
/// Размеры/смещения структур wintrust.h для x64, посчитаны вручную по
/// документации Microsoft (см. комментарий в WinTrustTypes.cs) и
/// перепроверены здесь — тот же метод, которым в WfpStructLayoutTests.cs
/// дважды пойманы реальные баги выравнивания ДО первого живого теста.
/// </summary>
public sealed class WinTrustStructLayoutTests
{
    [Fact]
    public void File_info_is_32_bytes_with_documented_offsets()
    {
        // cbStruct(4)+pad(4) + pcwszFilePath(8) + hFile(8) + pgKnownSubject(8)
        Assert.Equal(32, Marshal.SizeOf<WINTRUST_FILE_INFO>());
        Assert.Equal(0, Offset<WINTRUST_FILE_INFO>(nameof(WINTRUST_FILE_INFO.cbStruct)));
        Assert.Equal(8, Offset<WINTRUST_FILE_INFO>(nameof(WINTRUST_FILE_INFO.pcwszFilePath)));
        Assert.Equal(16, Offset<WINTRUST_FILE_INFO>(nameof(WINTRUST_FILE_INFO.hFile)));
        Assert.Equal(24, Offset<WINTRUST_FILE_INFO>(nameof(WINTRUST_FILE_INFO.pgKnownSubject)));
    }

    [Fact]
    public void Data_is_88_bytes_with_documented_offsets()
    {
        // Порядок и типы полей сверены построчно с learn.microsoft.com
        // (ns-wintrust-wintrust_data) — см. комментарий в WinTrustTypes.cs.
        Assert.Equal(88, Marshal.SizeOf<WINTRUST_DATA>());
        Assert.Equal(0, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.cbStruct)));
        Assert.Equal(8, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.pPolicyCallbackData)));
        Assert.Equal(16, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.pSIPClientData)));
        Assert.Equal(24, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwUIChoice)));
        Assert.Equal(28, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.fdwRevocationChecks)));
        Assert.Equal(32, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwUnionChoice)));
        Assert.Equal(40, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.pFile)));
        Assert.Equal(48, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwStateAction)));
        Assert.Equal(56, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.hWVTStateData)));
        Assert.Equal(64, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.pwszURLReference)));
        Assert.Equal(72, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwProvFlags)));
        Assert.Equal(76, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.dwUIContext)));
        Assert.Equal(80, Offset<WINTRUST_DATA>(nameof(WINTRUST_DATA.pSignatureSettings)));
    }

    [Fact]
    public void Action_generic_verify_v2_guid_matches_wintrust_h()
    {
        Assert.Equal(new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), WinTrustNative.ActionGenericVerifyV2);
    }

    private static int Offset<T>(string field) where T : struct => Marshal.OffsetOf<T>(field).ToInt32();
}
