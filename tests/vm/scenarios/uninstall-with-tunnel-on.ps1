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
#   3. туннель включён, процесс службы АБРУПТНО убит (Stop-Process -Force,
#      БЕЗ штатного off/OnStop) прямо перед удалением — имитирует "службу
#      убили/она упала" без графической остановки вообще: маршруты и
#      фильтры остаются осиротевшими точно так же, как при реально
#      зависшей и потом прибитой сборщиком мусора ОС службе, и всё равно
#      должны быть сняты doctor'ом.
#
#      ПЕРВАЯ версия этого сценария замораживала весь процесс службы
#      через NtSuspendProcess (все потоки), чтобы дословно воспроизвести
#      таймаут WaitForServiceStopped — живым прогоном (дважды, независимо
#      друг от друга) поймано, что это вешает сетевой стек ВСЕЙ гостевой
#      машины (видимо, замороженный поток держит блокировку ядра
#      WFP/wintun) настолько, что даже guest agent не восстанавливает
#      связь и with-rescue.sh откатывает VM на снимок. Ветка "не
#      остановилась за 30с -> Log()/MsgBox" (installer/vless-tunnel.iss)
#      поэтому проверена ТОЛЬКО код-ревью (тот же гейт UninstallSilent(),
#      что уже живьём проверен для другого MsgBox в этом же файле), не
#      живым тестом — риск повторной поломки стенда не окупается.
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

function Wait-PathGone {
    # unins000.exe не может удалить сам себя, пока выполняется — самоудаление
    # идёт через отдельный процесс-помощник, запускаемый ПОСЛЕ выхода
    # основного процесса, с небольшой задержкой. Фиксированного Start-Sleep
    # оказалось недостаточно (живым тестом дважды поймана гонка: папка
    # установки ещё на месте сразу после Start-Process -Wait) — опрос с
    # запасом по времени вместо гадания с константой.
    param([string]$Path, [int]$TimeoutSec = 20)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $Path)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return -not (Test-Path $Path)
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

    $appGone = Wait-PathGone $installDir 20
    Add-Step -Step 'scenario1: install dir fully removed (polled up to 20s)' -Expected $true -Actual $appGone -Pass $appGone
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

# === Сценарий 3: туннель включён, процесс службы убит абруптно (без off/OnStop) ===
Install-Fresh | Out-Null
$onResp3 = Start-TunnelOn
Add-Step -Step 'scenario3: tunnel on before abrupt-kill uninstall' -Expected $true -Actual ([bool]$onResp3.Ok) -Pass ([bool]$onResp3.Ok) -Info $onResp3.Error
$serverHost3 = $onResp3.Status.ServerHost

if ($onResp3.Ok) {
    $svcProc = Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue
    $killed = $false
    if ($svcProc) {
        Stop-Process -Id $svcProc.Id -Force
        $killed = $true
    }
    Add-Step -Step 'scenario3: service process killed abruptly (no graceful off/OnStop)' -Expected $true -Actual $killed -Pass $killed
    Start-Sleep -Seconds 2

    # Процесс мёртв, но служба ЗАРЕГИСТРИРОВАНА (sc delete ещё не звали) —
    # SCM увидит её как остановленную (не запущенную), sc stop/off пройдут
    # быстро и без эффекта (маршруты/фильтры физически некому снимать,
    # процесс уже мёртв) — ключевая проверка теста: doctor всё равно
    # снимает то, что абруптно убитый процесс не успел снять сам.
    $uninstExit3 = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
    Start-Sleep -Seconds 3
    Add-Step -Step 'scenario3: uninstall after abrupt kill exit code' -Expected 0 -Actual $uninstExit3 -Pass ($uninstExit3 -eq 0)

    $hostRouteGone3 = -not (Get-NetRoute -DestinationPrefix "$serverHost3/32" -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario3: host route gone despite abrupt kill' -Expected $true -Actual $hostRouteGone3 -Pass $hostRouteGone3

    $splitGone3 = Test-NoSplitRoutes
    Add-Step -Step 'scenario3: split /1 routes gone despite abrupt kill' -Expected $true -Actual $splitGone3 -Pass $splitGone3

    $noFilters3 = Test-NoOwnWfpFilters
    Add-Step -Step 'scenario3: no own WFP filters left despite abrupt kill' -Expected $true -Actual $noFilters3 -Pass ([bool]$noFilters3)

    $svc3Gone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
    Add-Step -Step 'scenario3: service unregistered' -Expected $true -Actual $svc3Gone -Pass $svc3Gone

    # Уборка стенда — не влияет на PASS/FAIL выше, только на случай, если
    # что-то из вышеперечисленного не подчистило всё до конца.
    Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    sc.exe delete vless-tunnel | Out-Null
    if (Test-Path $uninst) {
        Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction SilentlyContinue
    }
}
