# Стенд, живой тест ревью п.23 (3/3): туннель включён, процесс службы
# убит АБРУПТНО (Stop-Process -Force, без штатного off/OnStop) прямо
# перед удалением — имитирует "службу убили/она упала" без графической
# остановки вообще: маршруты и WFP-фильтры остаются осиротевшими точно
# так же, как при реально зависшей и потом прибитой ОС/диспетчером
# задач службе, и всё равно должны быть сняты doctor'ом
# (VlessTunnel.Service.exe doctor -> KillSwitch.Doctor +
# RouteManager.RemoveOwnRoutes, см. NetworkDoctor).
#
# ПЕРВАЯ версия этого теста замораживала весь процесс службы через
# NtSuspendProcess (все потоки), чтобы дословно воспроизвести таймаут
# WaitForServiceStopped — живым прогоном (дважды, независимо друг от
# друга) поймано, что это вешает сетевой стек ВСЕЙ гостевой машины
# (видимо, замороженный поток держит блокировку ядра WFP/wintun)
# настолько, что даже guest agent не восстанавливает связь и
# with-rescue.sh откатывает VM на снимок. Ветка "sc stop не уложился в
# 30с -> Log()/MsgBox" (installer/vless-tunnel.iss) поэтому проверена
# ТОЛЬКО код-ревью (тот же гейт UninstallSilent(), что уже живьём
# проверен для другого MsgBox в этом же файле), не живым тестом — риск
# повторной поломки стенда не окупается.
#
# Раньше это был один файл на все 3 сценария п.23 (uninstall-with-
# tunnel-on.ps1) — разбито на 3 независимых файла, свой снимок/откат на
# каждый (см. uninstall-tunnel-on-silent.ps1 за подробностями, почему).
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

$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
Add-Step -Step 'install exit code' -Expected 0 -Actual $p.ExitCode -Pass ($p.ExitCode -eq 0)

Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$onResp = $null
for ($a = 1; $a -le 3; $a++) {
    $onResp = Invoke-VtIpc -Cmd 'on'
    if ($onResp.Ok) { break }
}
Add-Step -Step 'tunnel on before abrupt-kill uninstall' -Expected $true -Actual ([bool]$onResp.Ok) -Pass ([bool]$onResp.Ok) -Info $onResp.Error
$serverHost = $onResp.Status.ServerHost

if ($onResp.Ok) {
    $svcProc = Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue
    $killed = $false
    if ($svcProc) {
        Stop-Process -Id $svcProc.Id -Force
        $killed = $true
    }
    Add-Step -Step 'service process killed abruptly (no graceful off/OnStop)' -Expected $true -Actual $killed -Pass $killed
    Start-Sleep -Seconds 2

    # Процесс мёртв, но служба ЗАРЕГИСТРИРОВАНА (sc delete ещё не звали) —
    # SCM увидит её как остановленную (не запущенную), sc stop/off пройдут
    # быстро и без эффекта (маршруты/фильтры физически некому снимать,
    # процесс уже мёртв) — ключевая проверка теста: doctor всё равно
    # снимает то, что абруптно убитый процесс не успел снять сам.
    $uninstExit = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
    Start-Sleep -Seconds 3
    Add-Step -Step 'uninstall after abrupt kill exit code' -Expected 0 -Actual $uninstExit -Pass ($uninstExit -eq 0)

    $hostRouteGone = -not (Get-NetRoute -DestinationPrefix "$serverHost/32" -ErrorAction SilentlyContinue)
    Add-Step -Step 'host route gone despite abrupt kill' -Expected $true -Actual $hostRouteGone -Pass $hostRouteGone

    $splitGone = Test-NoSplitRoutes
    Add-Step -Step 'split /1 routes gone despite abrupt kill' -Expected $true -Actual $splitGone -Pass $splitGone

    $noFilters = Test-NoOwnWfpFilters
    Add-Step -Step 'no own WFP filters left despite abrupt kill' -Expected $true -Actual $noFilters -Pass ([bool]$noFilters)

    $svcGone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
    Add-Step -Step 'service unregistered' -Expected $true -Actual $svcGone -Pass $svcGone

    # Уборка стенда — не влияет на PASS/FAIL выше, только на случай, если
    # что-то из вышеперечисленного не подчистило всё до конца.
    Get-Process -Name 'VlessTunnel.Service' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    sc.exe delete vless-tunnel | Out-Null
    if (Test-Path $uninst) {
        Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -ErrorAction SilentlyContinue
    }
}
