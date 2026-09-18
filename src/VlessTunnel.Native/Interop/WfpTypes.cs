using System.Runtime.InteropServices;

namespace VlessTunnel.Native.Interop;

/// <summary>
/// Структуры WFP (fwpmtypes.h/fwpmu.h) для kill-switch (план, 3.3/3.9).
/// Размеры/смещения посчитаны вручную по документации Microsoft для x64 и
/// перепроверены юнит-тестами (<c>tests/VlessTunnel.Native.Tests/WfpStructLayoutTests.cs</c>)
/// через <c>Marshal.SizeOf</c>/<c>OffsetOf</c> — тот же метод, которым в
/// этом же проекте были пойманы два реальных бага выравнивания в структурах
/// IP Helper (см. MibTypes.cs). Здесь структуры крупнее и вложены глубже,
/// поэтому проверка тем более обязательна ДО первого живого теста на стенде.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_DISPLAY_DATA0
{
    public nint name;         // LPWSTR
    public nint description;  // LPWSTR
}

/// <summary>FWP_BYTE_BLOB — размер данных + указатель на них.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWP_BYTE_BLOB
{
    public uint size;
    public nint data; // UINT8*
}

/// <summary>
/// FWP_CONDITION_VALUE0 / FWP_VALUE0 — размечены одинаково: тип-дискриминатор
/// (FWP_DATA_TYPE) + 8-байтный слот union'а. Слот храним как <c>ulong</c> —
/// для указателей (FWP_BYTE_BLOB*, FWP_V4_ADDR_AND_MASK* и т.д.) кладём туда
/// адрес через <see cref="AsPointer"/>, для целых — само число.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWP_CONDITION_VALUE0
{
    public uint type; // FWP_DATA_TYPE
    public ulong value;

    public static FWP_CONDITION_VALUE0 UInt8(byte v) => new() { type = FwpDataType.UInt8, value = v };
    public static FWP_CONDITION_VALUE0 UInt16(ushort v) => new() { type = FwpDataType.UInt16, value = v };
    public static FWP_CONDITION_VALUE0 UInt32(uint v) => new() { type = FwpDataType.UInt32, value = v };
    public static FWP_CONDITION_VALUE0 AsPointer(uint type, nint pointer) => new() { type = type, value = (ulong)pointer };
}

/// <summary>
/// FWP_DATA_TYPE (fwptypes.h). Значения сверены напрямую по документации
/// Microsoft (learn.microsoft.com/.../ne-fwptypes-fwp_data_type) — enum
/// начинается с FWP_EMPTY=0, поэтому FWP_UINT8=1, а не 0 (по памяти это
/// легко перепутать и получить тихо неверное условие фильтра, без ошибки
/// компиляции или выполнения — то же самое, что уже дважды случалось с
/// выравниванием структур IP Helper).
/// </summary>
internal static class FwpDataType
{
    public const uint Empty = 0;
    public const uint UInt8 = 1;
    public const uint UInt16 = 2;
    public const uint UInt32 = 3;
    public const uint UInt64 = 4;
    public const uint ByteArray16Type = 11;
    public const uint ByteBlobType = 12;
    public const uint V4AddrMask = 0x100;
    public const uint V6AddrMask = 0x101;
    public const uint RangeType = 0x102;
}

/// <summary>FWP_MATCH_TYPE.</summary>
internal static class FwpMatchType
{
    public const uint Equal = 0;
    public const uint NotEqual = 10;
    public const uint FlagsAllSet = 6;
}

/// <summary>FWPM_FILTER_CONDITION0 — одно условие фильтра.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_FILTER_CONDITION0
{
    public Guid fieldKey;
    public uint matchType; // FWP_MATCH_TYPE
    public FWP_CONDITION_VALUE0 conditionValue;
}

/// <summary>FWP_ACTION_TYPE — только значения, нужные kill-switch'у.</summary>
internal static class FwpActionType
{
    private const uint FlagTerminating = 0x00001000;
    public const uint Block = FlagTerminating | 0x0001;
    public const uint Permit = FlagTerminating | 0x0002;
}

/// <summary>
/// FWPM_ACTION0 — union { GUID filterType; GUID calloutKey; } после
/// 4-байтного type НЕ выравнивается до 8 (настоящий Win32 GUID имеет
/// выравнивание 4, не 8 — та же проверка, что уже потребовалась для
/// SOCKADDR_IN6/IN6_ADDR в MibTypes.cs), поэтому структура — 20 байт,
/// не 24.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_ACTION0
{
    public uint type; // FWP_ACTION_TYPE
    public Guid filterTypeOrCalloutKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_PROVIDER0
{
    public Guid providerKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public nint providerData;  // FWP_BYTE_BLOB*
    public nint serviceName;   // LPWSTR
}

[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_SUBLAYER0
{
    public Guid subLayerKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public nint providerKey;   // GUID*
    public nint providerData;  // FWP_BYTE_BLOB*
    public ushort weight;
}

/// <summary>
/// union{ UINT64 rawContext; GUID providerContextKey; } внутри FWPM_FILTER0 —
/// 16 байт (по более широкому члену, GUID), оба поля на смещении 0. Обычные
/// два подряд идущих поля тут были бы ошибкой: сдвинули бы все смещения
/// после этого union'а на 8 байт (тот же класс ошибки, что уже был пойман
/// на SOCKADDR_IN.sin_zero/IN6_ADDR в MibTypes.cs, но от противоположной
/// причины — там лишнее выравнивание, здесь лишний размер).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct FWPM_FILTER_CONTEXT_UNION0
{
    [FieldOffset(0)] public ulong rawContext;
    [FieldOffset(0)] public Guid providerContextKey;
}

/// <summary>
/// FWPM_FILTER0 — самая крупная структура kill-switch'а. 200 байт на x64;
/// точный layout (включая то, что providerData — встроенный FWP_BYTE_BLOB,
/// а не указатель) сверен с черновиком в vm/rescue.ps1 (написан заранее на
/// этапе 0а по документации для функции удаления фильтров) и независимо
/// перепроверен юнит-тестом.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FWPM_FILTER0
{
    public Guid filterKey;
    public FWPM_DISPLAY_DATA0 displayData;
    public uint flags;
    public nint providerKey;           // GUID*, nullable
    public FWP_BYTE_BLOB providerData; // встроенная структура, не указатель
    public Guid layerKey;
    public Guid subLayerKey;
    public FWP_CONDITION_VALUE0 weight;
    public uint numFilterConditions;
    public nint filterCondition;       // FWPM_FILTER_CONDITION0*
    public FWPM_ACTION0 action;
    public FWPM_FILTER_CONTEXT_UNION0 context; // rawContext=0 у нас всегда — provider context не используется
    public nint reserved;              // GUID*, nullable
    public ulong filterId;
    public FWP_CONDITION_VALUE0 effectiveWeight;
}
