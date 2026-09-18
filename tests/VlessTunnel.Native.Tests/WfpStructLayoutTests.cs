using System.Runtime.InteropServices;
using VlessTunnel.Native.Interop;
using Xunit;

namespace VlessTunnel.Native.Tests;

/// <summary>
/// Размеры/смещения WFP-структур (fwpmtypes.h) для x64 — посчитаны вручную
/// в комментариях WfpTypes.cs, перепроверены здесь тем же методом, которым
/// на структурах IP Helper (см. StructLayoutTests.cs) уже дважды пойманы
/// реальные баги выравнивания ДО первого живого теста на стенде.
/// </summary>
public sealed class WfpStructLayoutTests
{
    [Fact]
    public void Display_data_is_two_pointers_16_bytes() => Assert.Equal(16, Marshal.SizeOf<FWPM_DISPLAY_DATA0>());

    [Fact]
    public void Byte_blob_is_16_bytes() => Assert.Equal(16, Marshal.SizeOf<FWP_BYTE_BLOB>());

    [Fact]
    public void V4_addr_and_mask_is_8_bytes() => Assert.Equal(8, Marshal.SizeOf<FWP_V4_ADDR_AND_MASK>());

    [Fact]
    public void V4_addr_and_mask_from_cidr_is_host_order()
    {
        // 10.0.0.0/8 -> addr=0x0A000000, mask=0xFF000000 (host order — на x64
        // это значит младший байт в памяти = 0x00, старший = 0x0A/0xFF).
        var v = FWP_V4_ADDR_AND_MASK.FromCidr(System.Net.IPAddress.Parse("10.0.0.0"), 8);
        Assert.Equal(0x0A000000u, v.addr);
        Assert.Equal(0xFF000000u, v.mask);
    }

    [Fact]
    public void V4_addr_and_mask_slash_24()
    {
        var v = FWP_V4_ADDR_AND_MASK.FromCidr(System.Net.IPAddress.Parse("192.168.1.0"), 24);
        Assert.Equal(0xC0A80100u, v.addr);
        Assert.Equal(0xFFFFFF00u, v.mask);
    }

    [Fact]
    public void V6_addr_and_mask_is_17_bytes() => Assert.Equal(17, Marshal.SizeOf<FWP_V6_ADDR_AND_MASK>());

    [Fact]
    public void V6_addr_and_mask_from_cidr()
    {
        var v = FWP_V6_ADDR_AND_MASK.FromCidr(System.Net.IPAddress.Parse("fc00::"), 7);
        Assert.Equal(0xFC, v.a0);
        Assert.Equal(0, v.a1);
        Assert.Equal(7, v.prefixLength);
    }

    [Fact]
    public void Condition_value_is_16_bytes_type_then_value() => Assert.Equal(16, Marshal.SizeOf<FWP_CONDITION_VALUE0>());

    [Fact]
    public void Filter_condition_is_40_bytes()
    {
        Assert.Equal(40, Marshal.SizeOf<FWPM_FILTER_CONDITION0>());
        Assert.Equal(0, Marshal.OffsetOf<FWPM_FILTER_CONDITION0>(nameof(FWPM_FILTER_CONDITION0.fieldKey)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<FWPM_FILTER_CONDITION0>(nameof(FWPM_FILTER_CONDITION0.matchType)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<FWPM_FILTER_CONDITION0>(nameof(FWPM_FILTER_CONDITION0.conditionValue)).ToInt32());
    }

    [Fact]
    public void Action_is_20_bytes_guid_not_aligned_to_8()
    {
        // Настоящий Win32 GUID выровнен по 4 байтам, не по 8 — union {GUID}
        // после UINT32 type идёт СРАЗУ, без паддинга (в отличие от указателя).
        Assert.Equal(20, Marshal.SizeOf<FWPM_ACTION0>());
        Assert.Equal(0, Marshal.OffsetOf<FWPM_ACTION0>(nameof(FWPM_ACTION0.type)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<FWPM_ACTION0>(nameof(FWPM_ACTION0.filterTypeOrCalloutKey)).ToInt32());
    }

    [Fact]
    public void Context_union_is_16_bytes_both_fields_at_offset_0()
    {
        Assert.Equal(16, Marshal.SizeOf<FWPM_FILTER_CONTEXT_UNION0>());
        Assert.Equal(0, Marshal.OffsetOf<FWPM_FILTER_CONTEXT_UNION0>(nameof(FWPM_FILTER_CONTEXT_UNION0.rawContext)).ToInt32());
        Assert.Equal(0, Marshal.OffsetOf<FWPM_FILTER_CONTEXT_UNION0>(nameof(FWPM_FILTER_CONTEXT_UNION0.providerContextKey)).ToInt32());
    }

    [Fact]
    public void Provider_is_56_bytes_with_documented_offsets()
    {
        Assert.Equal(56, Marshal.SizeOf<FWPM_PROVIDER0>());
        Assert.Equal(0, Offset<FWPM_PROVIDER0>(nameof(FWPM_PROVIDER0.providerKey)));
        Assert.Equal(16, Offset<FWPM_PROVIDER0>(nameof(FWPM_PROVIDER0.displayData)));
        Assert.Equal(32, Offset<FWPM_PROVIDER0>(nameof(FWPM_PROVIDER0.flags)));
        Assert.Equal(40, Offset<FWPM_PROVIDER0>(nameof(FWPM_PROVIDER0.providerData)));
        Assert.Equal(48, Offset<FWPM_PROVIDER0>(nameof(FWPM_PROVIDER0.serviceName)));
    }

    [Fact]
    public void Sublayer_is_64_bytes_with_documented_offsets()
    {
        Assert.Equal(64, Marshal.SizeOf<FWPM_SUBLAYER0>());
        Assert.Equal(0, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.subLayerKey)));
        Assert.Equal(16, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.displayData)));
        Assert.Equal(32, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.flags)));
        Assert.Equal(40, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.providerKey)));
        Assert.Equal(48, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.providerData)));
        Assert.Equal(56, Offset<FWPM_SUBLAYER0>(nameof(FWPM_SUBLAYER0.weight)));
    }

    [Fact]
    public void Filter_is_200_bytes_with_documented_offsets()
    {
        // Смещения providerKey=40 и subLayerKey=80 независимо совпадают с
        // расчётом, уже сделанным (и не проверенным вживую) в vm/rescue.ps1
        // при написании функции удаления фильтров по GUID — совпадение
        // с ДРУГИМ, ранее написанным выводом усиливает уверенность.
        Assert.Equal(200, Marshal.SizeOf<FWPM_FILTER0>());
        Assert.Equal(0, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.filterKey)));
        Assert.Equal(16, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.displayData)));
        Assert.Equal(32, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.flags)));
        Assert.Equal(40, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.providerKey)));
        Assert.Equal(48, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.providerData)));
        Assert.Equal(64, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.layerKey)));
        Assert.Equal(80, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.subLayerKey)));
        Assert.Equal(96, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.weight)));
        Assert.Equal(112, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.numFilterConditions)));
        Assert.Equal(120, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.filterCondition)));
        Assert.Equal(128, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.action)));
        Assert.Equal(152, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.context)));
        Assert.Equal(168, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.reserved)));
        Assert.Equal(176, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.filterId)));
        Assert.Equal(184, Offset<FWPM_FILTER0>(nameof(FWPM_FILTER0.effectiveWeight)));
    }

    private static int Offset<T>(string field) where T : struct => Marshal.OffsetOf<T>(field).ToInt32();
}
