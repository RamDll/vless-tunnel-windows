# Стенд, живой тест ревью п.23: "Удаление должно явно выключать активный
# туннель". Раньше туннель сворачивался ПОБОЧНЫМ эффектом sc stop (OnStop
# -> OffAsync внутри самой службы) — работает, только пока служба
# успевает уложиться в таймаут SCM. Теперь StopAndCleanupServiceForUninstall
# (installer/vless-tunnel.iss) явно гасит туннель через
# "VlessTunnel.Cli.exe off", явно закрывает трей тем же способом, что
# self-update (SelfUpdater.CloseRunningTray, теперь public + команда
# "close-tray"), и doctor (VlessTunnel.Service.exe doctor) снимает не
# только WFP-фильтры, но и НАШИ маршруты по метке OwnRouteProtocol
# (RouteManager.RemoveOwnRoutes) — даже если сама остановка службы
# зависла.
#
# Все проверки — структурными данными (Get-NetRoute/Get-Service/
# Get-Process), WFP — через netsh wfp show filters (XML-файл, грепаем
# GUID, не текст "успешно"), ни разу текстом человеческого вывода.
#
# Три сценария из ревью п.23 в одном файле (общая сборка/установка):
#   1. тихое удаление ПРИ ВКЛЮЧЁННОМ туннеле (мастер и /VERYSILENT
#      выполняют идентичный Pascal-код StopAndCleanupServiceForUninstall,
#      разница только в MsgBox/диалогах — здесь прогоняем /VERYSILENT,
#      т.к. интерактивный мастер в headless-автоматизации некем "кликать")
#   2. удаление при уже остановленной службе — не должно падать
#   3. удаление при включённом туннеле и НАМЕРЕННО зависшей остановке
#      (NtSuspendProcess замораживает ВСЕ потоки процесса службы — то же
#      самое, что видит WaitForServiceStopped при реально зависшей
#      службе) — маршруты и фильтры всё равно должны быть сняты doctor'ом.
#
# Дот-сорсится из run-test.ps1 — Add-Step/Invoke-VtIpc уже в scope.

$ErrorActionPreference = 'Stop'
$installDir = 'C:\Program Files\vless-tunnel'
$uninst = Join-Path $installDir 'unins000.exe'

Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (Test-Path $uninst) {
    Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Seconds 2
}

& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\dev\bin\guest-build.ps1' -Version '0.0.0-scenario'
if ($LASTEXITCODE -ne 0) {
    Add-Step -Step 'build produced Setup.exe' -Expected 0 -Actual $LASTEXITCODE -Pass $false -ExitCode $LASTEXITCODE
    return
}
$setup = 'C:\dev\build\vless-tunnel-setup.exe'

if (-not (Test-Path 'C:\ProgramData\vless-tunnel\link.txt')) {
    Add-Step -Step 'link configured (prerequisite)' -Expected 'present' -Actual 'absent' -Pass $false `
        -Info 'no persisted link.txt — cannot exercise tunnel-on uninstall'
    return
}

function Test-NoOwnWfpFilters {
    # ProviderGuid/SublayerGuid — VlessTunnel.Native/KillSwitch.cs. Программы
    # на машине после удаления уже нет — свой exe для перечисления фильтров
    # позвать нечем, поэтому netsh wfp show filters (независимый от продукта
    # системный инструмент), грепаем по GUID в XML, не по словам.
    $xmlPath = "C:\dev\logs\wfp-$([Guid]::NewGuid().ToString('N')).xml"
    netsh wfp show filters file="$xmlPath" | Out-Null
    if (-not (Test-Path $xmlPath)) { return $null }
    $content = Get-Content $xmlPath -Raw
    Remove-Item $xmlPath -Force -ErrorAction SilentlyContinue
    $hasProvider = $content -match '64291c58-52b0-4fae-8d47-8af2cbec87c3'
    $hasSublayer = $content -match 'f5e23d33-098e-444a-a312-7b190a764453'
    return (-not $hasProvider) -and (-not $hasSublayer)
}

function Test-NoSplitRoutes {
    (-not (Get-NetRoute -DestinationPrefix '0.0.0.0/1' -ErrorAction SilentlyContinue)) -and
    (-not (Get-NetRoute -DestinationPrefix '128.0.0.0/1' -ErrorAction SilentlyContinue))
}

function Install-Fresh {
    $p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Start-Sleep -Seconds 3
    return $p.ExitCode
}

function Start-TunnelOn {
    Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $resp = $null
    for ($a = 1; $a -le 3; $a++) {
        $resp = Invoke-VtIpc -Cmd 'on'
        if ($resp.Ok) { break }
    }
    return $resp
}

# === Сценарий 1: тихое удаление при включённом туннеле, трее и запущенной службе ===
$installExit = Install-Fresh
Add-Step -Step 'scenario1: install exit code' -Expected 0 -Actual $installExit -Pass ($installExit -eq 0)

Start-Process (Join-Path $installDir 'VlessTunnel.Tray.exe')
Start-Sleep -Seconds 2

$onResp = Start-TunnelOn
Add-Step -Step 'scenario1: tunnel on before uninstall' -Expected $true -Actual ([bool]$onResp.Ok) -Pass ([bool]$onResp.Ok) -Info $onResp.Error
$serverHost = $onResp.Status.ServerHost

if ($onResp.Ok) {
    $uninstExit = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
    Start-Sleep -Seconds 3

    Add-Step -Step 'scenario1: uninstall exit code' -Expected 0 -Actual $uninstExit -Pass ($uninstExit -eq 0)

    $hostRouteGone = -not (Get-NetRoute -DestinationPrefix "$serverHost/32" -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario1: host route to server gone' -Expected $true -Actual $hostRouteGone -Pass $hostRouteGone

    $splitGone = Test-NoSplitRoutes
    Add-Step -Step 'scenario1: split /1 default routes gone' -Expected $true -Actual $splitGone -Pass $splitGone

    $noFilters = Test-NoOwnWfpFilters
    Add-Step -Step 'scenario1: no own WFP filters left' -Expected $true -Actual $noFilters -Pass ([bool]$noFilters)

    $xrayGone = -not (Get-Process -Name 'xray' -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario1: xray.exe not running' -Expected $true -Actual $xrayGone -Pass $xrayGone

    $trayGone = -not (Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario1: tray closed by uninstall' -Expected $true -Actual $trayGone -Pass $trayGone

    $svcGone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario1: service unregistered' -Expected $true -Actual $svcGone -Pass $svcGone

    $appGone = -not (Test-Path $installDir)
    Add-Step -Step 'scenario1: install dir fully removed' -Expected $true -Actual $appGone -Pass $appGone
} else {
    Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $uninst) { Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait }
}

# === Сценарий 2: удаление при уже остановленной службе — не должно падать ===
Install-Fresh | Out-Null
Stop-Service -Name 'vless-tunnel' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$svcState = (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue).Status
$uninstExit2 = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
Start-Sleep -Seconds 2

Add-Step -Step 'scenario2: service was stopped before uninstall' -Expected 'Stopped' -Actual "$svcState" -Pass ($svcState -eq 'Stopped')
Add-Step -Step 'scenario2: uninstall with already-stopped service exit code' -Expected 0 -Actual $uninstExit2 -Pass ($uninstExit2 -eq 0)
$svc2Gone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
Add-Step -Step 'scenario2: service unregistered' -Expected $true -Actual $svc2Gone -Pass $svc2Gone

# === Сценарий 3: включённый туннель + намеренно зависшая остановка службы ===
Add-Type -Namespace VtTest -Name ProcCtl -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("ntdll.dll")]
public static extern uint NtSuspendProcess(IntPtr processHandle);
[System.Runtime.InteropServices.DllImport("ntdll.dll")]
public static extern uint NtResumeProcess(IntPtr processHandle);
'@

Install-Fresh | Out-Null
$onResp3 = Start-TunnelOn
Add-Step -Step 'scenario3: tunnel on before hung-stop uninstall' -Expected $true -Actual ([bool]$onResp3.Ok) -Pass ([bool]$onResp3.Ok) -Info $onResp3.Error
$serverHost3 = $onResp3.Status.ServerHost

if ($onResp3.Ok) {
    $svcProc = Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue
    $suspended = $false
    if ($svcProc) {
        [VtTest.ProcCtl]::NtSuspendProcess($svcProc.Handle) | Out-Null
        $suspended = $true
    }
    Add-Step -Step 'scenario3: service process suspended for test' -Expected $true -Actual $suspended -Pass $suspended

    $uninstLog = 'C:\dev\logs\uninstall-hung.log'
    Remove-Item $uninstLog -Force -ErrorAction SilentlyContinue
    # off (до 90с своего IPC-таймаута, служба заморожена — не ответит) +
    # sc stop + WaitForServiceStopped (30с) + doctor + sc delete — с запасом.
    $p3 = Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstLog" -PassThru
    $finished = $p3.WaitForExit(240000)

    if ($svcProc -and -not $svcProc.HasExited) {
        [VtTest.ProcCtl]::NtResumeProcess($svcProc.Handle) | Out-Null
    }
    if (-not $finished) { try { $p3.WaitForExit(30000) | Out-Null } catch {} }

    Add-Step -Step 'scenario3: uninstall (with suspended service) finished' -Expected $true -Actual ($finished -or $p3.HasExited) -Pass ($finished -or $p3.HasExited)

    $logContent = if (Test-Path $uninstLog) { Get-Content $uninstLog -Raw } else { '' }
    $loggedTimeout = $logContent -match 'не остановилась'
    Add-Step -Step 'scenario3: stop timeout recorded via Log() in .iss' -Expected $true -Actual $loggedTimeout -Pass $loggedTimeout

    Start-Sleep -Seconds 2
    if ($svcProc -and -not $svcProc.HasExited) { Stop-Process -Id $svcProc.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2

    $hostRouteGone3 = -not (Get-NetRoute -DestinationPrefix "$serverHost3/32" -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario3: host route gone despite hung stop' -Expected $true -Actual $hostRouteGone3 -Pass $hostRouteGone3

    $splitGone3 = Test-NoSplitRoutes
    Add-Step -Step 'scenario3: split /1 routes gone despite hung stop' -Expected $true -Actual $splitGone3 -Pass $splitGone3

    $noFilters3 = Test-NoOwnWfpFilters
    Add-Step -Step 'scenario3: no own WFP filters left despite hung stop' -Expected $true -Actual $noFilters3 -Pass ([bool]$noFilters3)

    # Уборка стенда — не влияет на PASS/FAIL выше, только чтобы следующий
    # прогон сценария стартовал с чистого состояния (зависший процесс мог
    # не дать sc delete снести регистрацию вовремя).
    Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    sc.exe delete vless-tunnel | Out-Null
    if (Test-Path $uninst) {
        Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction SilentlyContinue
    }
}
