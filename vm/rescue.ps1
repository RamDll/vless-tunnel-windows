# "Аварийный выключатель" (план, 3.9 / 5.5). Идемпотентный, не зависит от
# программы vless-tunnel (может выполняться и после того, как её файлы уже
# удалены вручную) — использует только встроенные средства Windows и
# фиксированные GUID/имена из vm/pinned-versions.txt, секция [wfp-guids].
#
# Снимает: службу и xray.exe, WFP-фильтры/sublayer/provider по GUID,
# правила брандмауэра по группе (альтернативный вариант kill-switch из 3.9),
# СВОИ маршруты по метке OwnRouteProtocol (хост-маршрут до сервера,
# split-default /1+/1 — ревью п.23: без этого, если программа уже
# удалена, снять их нечем), DNS на физических интерфейсах, планировщик
# vt-rescue, и на всякий случай — заново включает отключённые сетевые
# адаптеры и обновляет DHCP-аренду.
#
# ПРИМЕЧАНИЕ: блок WFP ниже написан заранее, на этапе 0а, когда в системе
# ещё нет ни одного реального фильтра (Core/Service появятся в этапах 1/3).
# P/Invoke-маршалинг FWPM_FILTER0 сверен по документации, но не проверен
# вживую против настоящих фильтров — обязательно перепроверить на этапе 3
# (критерий приёмки "doctor снимает фильтры").

param(
    [string]$LogDir = 'C:\dev\logs'
)

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$logFile = Join-Path $LogDir "rescue-$(Get-Date -Format yyyyMMdd-HHmmss).log"
Start-Transcript -Path $logFile -Append

$ProviderGuid = '64291c58-52b0-4fae-8d47-8af2cbec87c3'
$SublayerGuid = 'f5e23d33-098e-444a-a312-7b190a764453'
$FirewallGroup = 'vless-tunnel'
$ServiceName = 'vless-tunnel'   # будущее имя службы (этап 2+); пока не существует — не ошибка

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "=== $Name ==="
    try {
        & $Body
        Write-Host "RESULT: $Name OK"
    } catch {
        Write-Host "RESULT: $Name FAILED (non-fatal): $_"
    }
}

Step 'Остановить службу vless-tunnel (если есть)' {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }
}

Step 'Убить xray.exe (если запущен)' {
    Get-Process -Name 'xray' -ErrorAction SilentlyContinue | Stop-Process -Force
}

Step 'Снять задачу планировщика vt-rescue' {
    Unregister-ScheduledTask -TaskName 'vt-rescue' -Confirm:$false -ErrorAction SilentlyContinue
}

Step 'Снять правила брандмауэра по группе' {
    Get-NetFirewallRule -Group $FirewallGroup -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

Step 'Снять WFP-фильтры/sublayer/provider по GUID (best-effort)' {
    $src = @'
using System;
using System.Runtime.InteropServices;

public static class Wfp {
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmEngineOpen0(
        [MarshalAs(UnmanagedType.LPWStr)] string serverName,
        uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmEngineClose0(IntPtr engineHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterCreateEnumHandle0(
        IntPtr engineHandle, IntPtr enumTemplate, out IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterEnum0(
        IntPtr engineHandle, IntPtr enumHandle, uint numEntriesRequested,
        out IntPtr entries, out uint numEntriesReturned);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterDestroyEnumHandle0(IntPtr engineHandle, IntPtr enumHandle);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmFilterDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, ref Guid key);

    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    public static extern uint FwpmProviderDeleteByKey0(IntPtr engineHandle, ref Guid key);

    // Только начало FWPM_FILTER0 нужно нам (filterKey, providerKey*, ..., subLayerKey);
    // остальные поля читаем как есть по смещениям через Marshal, без полной структуры.
    public static int RemoveByProviderOrSublayer(Guid providerGuid, Guid sublayerGuid, out string log) {
        var sb = new System.Text.StringBuilder();
        int removed = 0;
        IntPtr engine;
        uint hr = FwpmEngineOpen0(null, 10 /*RPC_C_AUTHN_WINNT*/, IntPtr.Zero, IntPtr.Zero, out engine);
        if (hr != 0) { log = "FwpmEngineOpen0 failed: 0x" + hr.ToString("X"); return -1; }
        try {
            IntPtr enumHandle;
            hr = FwpmFilterCreateEnumHandle0(engine, IntPtr.Zero, out enumHandle);
            if (hr != 0) { log = "FwpmFilterCreateEnumHandle0 failed: 0x" + hr.ToString("X"); return -1; }
            try {
                IntPtr entries; uint returned;
                hr = FwpmFilterEnum0(engine, enumHandle, 4096, out entries, out returned);
                if (hr != 0) { log = "FwpmFilterEnum0 failed: 0x" + hr.ToString("X"); return -1; }
                IntPtr[] filterPtrs = new IntPtr[returned];
                if (returned > 0) Marshal.Copy(entries, filterPtrs, 0, (int)returned);
                for (int i = 0; i < returned; i++) {
                    IntPtr f = filterPtrs[i];
                    // filterKey: смещение 0 (GUID, 16 байт)
                    Guid filterKey = (Guid)Marshal.PtrToStructure(f, typeof(Guid));
                    // providerKey*: после filterKey(16) + FWPM_DISPLAY_DATA0(2 указателя=16 на x64) + flags(4, +4 паддинг) = смещение 40
                    IntPtr providerKeyPtr = Marshal.ReadIntPtr(f, 40);
                    Guid providerKey = Guid.Empty;
                    bool hasProvider = providerKeyPtr != IntPtr.Zero;
                    if (hasProvider) providerKey = (Guid)Marshal.PtrToStructure(providerKeyPtr, typeof(Guid));
                    // subLayerKey: providerKey*(8) + FWP_BYTE_BLOB(4+4pad+8=16) + layerKey(16) = смещение 40+8+16+16 = 80
                    Guid subLayerKey = (Guid)Marshal.PtrToStructure(new IntPtr(f.ToInt64() + 80), typeof(Guid));

                    if ((hasProvider && providerKey == providerGuid) || subLayerKey == sublayerGuid) {
                        Guid keyCopy = filterKey;
                        uint dhr = FwpmFilterDeleteByKey0(engine, ref keyCopy);
                        sb.AppendLine("filter " + filterKey + " delete=0x" + dhr.ToString("X"));
                        if (dhr == 0) removed++;
                    }
                }
            } finally {
                FwpmFilterDestroyEnumHandle0(engine, enumHandle);
            }

            Guid sl = sublayerGuid;
            uint slhr = FwpmSubLayerDeleteByKey0(engine, ref sl);
            sb.AppendLine("sublayer delete=0x" + slhr.ToString("X"));

            Guid pv = providerGuid;
            uint pvhr = FwpmProviderDeleteByKey0(engine, ref pv);
            sb.AppendLine("provider delete=0x" + pvhr.ToString("X"));
        } finally {
            FwpmEngineClose0(engine);
        }
        log = sb.ToString();
        return removed;
    }
}
'@
    Add-Type -TypeDefinition $src -ErrorAction Stop
    $log = ''
    $removed = [Wfp]::RemoveByProviderOrSublayer([Guid]$ProviderGuid, [Guid]$SublayerGuid, [ref]$log)
    Write-Host $log
    Write-Host "WFP filters removed: $removed"
}

# Ревью п.23: doctor продукта (NetworkDoctor.Run -> RouteManager.
# RemoveOwnRoutes) снимает маршруты по этой же метке — но rescue.ps1
# существует именно для случая "программы уже нет, звать doctor нечем"
# (план, 3.9: "может выполняться и после того, как её файлы уже удалены
# вручную"). Без этого шага хост-маршрут до сервера и split-default
# /1+/1 могли остаться в таблице маршрутов навсегда. Перебор
# GetIpForwardTable2 по системной таблице, тот же принцип, что и снятие
# WFP-фильтров выше (не память процесса, не файлы программы).
Step 'Снять СВОИ маршруты по метке OwnRouteProtocol (best-effort)' {
    $src = @'
using System;
using System.Runtime.InteropServices;

public static class Routes {
    [DllImport("iphlpapi.dll")]
    public static extern uint GetIpForwardTable2(ushort addressFamily, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern void FreeMibTable(IntPtr table);

    [DllImport("iphlpapi.dll")]
    public static extern uint DeleteIpForwardEntry2(IntPtr route);

    // MIB_IPFORWARD_ROW2 (netioapi.h): 104 байта на x64, Protocol на
    // смещении 88 — оба числа сверены отдельным xunit-тестом
    // (StructLayoutTests.Mib_ipforward_row2_is_104_bytes_with_documented_offsets)
    // в основном коде продукта (VlessTunnel.Native), не по памяти здесь.
    // Table[0] в MIB_IPFORWARD_TABLE2 начинается со смещения 8 (ULONG
    // NumEntries + 4 байта паддинга до 8-байтового выравнивания NET_LUID).
    private const int RowSize = 104;
    private const int HeaderSize = 8;
    private const int ProtocolOffset = 88;
    public const int OwnRouteProtocol = 11000;

    public static int RemoveOwnRoutes(out string log) {
        var sb = new System.Text.StringBuilder();
        int removed = 0;
        IntPtr table;
        uint hr = GetIpForwardTable2(0 /*AF_UNSPEC*/, out table);
        if (hr != 0) { log = "GetIpForwardTable2 failed: 0x" + hr.ToString("X"); return -1; }
        try {
            int numEntries = Marshal.ReadInt32(table);
            sb.AppendLine("routes enumerated: " + numEntries);
            for (int i = 0; i < numEntries; i++) {
                IntPtr row = new IntPtr(table.ToInt64() + HeaderSize + (long)i * RowSize);
                int protocol = Marshal.ReadInt32(row, ProtocolOffset);
                if (protocol != OwnRouteProtocol) continue;
                uint dhr = DeleteIpForwardEntry2(row);
                sb.AppendLine("route[" + i + "] delete=0x" + dhr.ToString("X"));
                if (dhr == 0) removed++;
            }
        } finally {
            FreeMibTable(table);
        }
        log = sb.ToString();
        return removed;
    }
}
'@
    Add-Type -TypeDefinition $src -ErrorAction Stop
    $log = ''
    $removed = [Routes]::RemoveOwnRoutes([ref]$log)
    Write-Host $log
    Write-Host "Own routes removed: $removed"
}

Step 'Включить отключённые сетевые адаптеры' {
    Get-NetAdapter | Where-Object Status -eq 'Disabled' | Enable-NetAdapter -Confirm:$false
}

## Стенд, п.1 (задним числом — найдено живым тестом п.2): vt-mgmt (адаптер
## управляющего канала, см. vmctl.sh) ИСКЛЮЧЁН из обоих шагов ниже.
## release/renew DHCP именно на нём рвёт саму SSH-сессию, через которую
## rescue.ps1 запущен, — ровно тот класс проблемы, который отдельный
## управляющий адаптер должен был исключить. Поймано на реальном прогоне:
## run-test.ps1 обрывался с exit 255 ровно на этом шаге rescue.ps1 (вызванного
## из finally), отчёт не успевал записаться.
Step 'Сбросить DNS физических адаптеров на автомат (DHCP), кроме vt-mgmt' {
    Get-DnsClientServerAddress -AddressFamily IPv4 | Where-Object {
        (Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue).Name -ne 'vt-mgmt'
    } | ForEach-Object {
        Set-DnsClientServerAddress -InterfaceIndex $_.InterfaceIndex -ResetServerAddresses -ErrorAction SilentlyContinue
    }
}

Step 'Обновить DHCP-аренду, кроме vt-mgmt' {
    Get-NetAdapter | Where-Object { $_.Name -ne 'vt-mgmt' -and $_.Status -eq 'Up' } | ForEach-Object {
        ipconfig /release $_.InterfaceAlias | Out-Null
        ipconfig /renew $_.InterfaceAlias | Out-Null
    }
}

Step 'Сбросить маршрут по умолчанию, если он потерян' {
    $hasDefault = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue
    if (-not $hasDefault) {
        # DHCP renew выше обычно восстанавливает шлюз сам; если нет —
        # дальше уже смотрит "doctor"/человек, rescue.ps1 не гадает адрес.
        Write-Host 'Внимание: маршрут по умолчанию всё ещё отсутствует после DHCP renew.'
    }
}

Write-Host 'RESULT: RESCUE DONE'
Stop-Transcript
