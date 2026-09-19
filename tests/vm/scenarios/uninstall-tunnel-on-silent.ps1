# Стенд, живой тест ревью п.23 (1/3): тихое (/VERYSILENT) удаление ПРИ
# ВКЛЮЧЁННОМ туннеле, запущенном трее и запущенной службе.
#
# Раньше туннель сворачивался ПОБОЧНЫМ эффектом sc stop (OnStop ->
# OffAsync внутри самой службы). Теперь StopAndCleanupServiceForUninstall
# (installer/vless-tunnel.iss) явно гасит туннель через
# "VlessTunnel.Cli.exe off", явно закрывает трей тем же способом, что
# self-update (SelfUpdater.CloseRunningTray, теперь public + команда
# "close-tray"), и doctor (VlessTunnel.Service.exe doctor) снимает не
# только WFP-фильтры, но и НАШИ маршруты по метке OwnRouteProtocol
# (RouteManager.RemoveOwnRoutes).
#
# Мастер (интерактивный) и /VERYSILENT выполняют идентичный Pascal-код —
# разница только в показе MsgBox/диалогов. Здесь прогоняем /VERYSILENT,
# т.к. в headless-автоматизации некому "кликать" мастер.
#
# Раньше это был один файл на все 3 сценария п.23 (uninstall-with-
# tunnel-on.ps1) — три install/on/uninstall цикла подряд в одном запуске
# with-rescue.sh оказались слишком тяжёлыми для проверки связи в конце
# (живым прогоном дважды поймано: сеть гостя не восстанавливалась после
# всех трёх циклов, хотя каждый цикл ПО ОТДЕЛЬНОСТИ проверялся чистым).
# Разбито на 3 независимых файла — свой снимок/откат на каждый, поломка
# одного не хоронит уже подтверждённый результат другого.
#
# Все проверки — структурными данными (Get-NetRoute/Get-Service/
# Get-Process), WFP — через netsh wfp show filters (XML-файл, грепаем
# GUID, не текст "успешно").
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
    # оказалось недостаточно (живым тестом поймана гонка: папка установки
    # ещё на месте сразу после Start-Process -Wait) — опрос с запасом по
    # времени вместо гадания с константой.
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

$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
Add-Step -Step 'install exit code' -Expected 0 -Actual $p.ExitCode -Pass ($p.ExitCode -eq 0)

Start-Process (Join-Path $installDir 'VlessTunnel.Tray.exe')
Start-Sleep -Seconds 2

Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$onResp = $null
for ($a = 1; $a -le 3; $a++) {
    $onResp = Invoke-VtIpc -Cmd 'on'
    if ($onResp.Ok) { break }
}
Add-Step -Step 'tunnel on before uninstall' -Expected $true -Actual ([bool]$onResp.Ok) -Pass ([bool]$onResp.Ok) -Info $onResp.Error
$serverHost = $onResp.Status.ServerHost

if ($onResp.Ok) {
    $uninstExit = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
    Start-Sleep -Seconds 3

    Add-Step -Step 'uninstall exit code' -Expected 0 -Actual $uninstExit -Pass ($uninstExit -eq 0)

    $hostRouteGone = -not (Get-NetRoute -DestinationPrefix "$serverHost/32" -ErrorAction SilentlyContinue)
    Add-Step -Step 'host route to server gone' -Expected $true -Actual $hostRouteGone -Pass $hostRouteGone

    $splitGone = Test-NoSplitRoutes
    Add-Step -Step 'split /1 default routes gone' -Expected $true -Actual $splitGone -Pass $splitGone

    $noFilters = Test-NoOwnWfpFilters
    Add-Step -Step 'no own WFP filters left' -Expected $true -Actual $noFilters -Pass ([bool]$noFilters)

    $xrayGone = -not (Get-Process -Name 'xray' -ErrorAction SilentlyContinue)
    Add-Step -Step 'xray.exe not running' -Expected $true -Actual $xrayGone -Pass $xrayGone

    $trayGone = -not (Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue)
    Add-Step -Step 'tray closed by uninstall' -Expected $true -Actual $trayGone -Pass $trayGone

    $svcGone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
    Add-Step -Step 'service unregistered' -Expected $true -Actual $svcGone -Pass $svcGone

    $appGone = Wait-PathGone $installDir 20
    Add-Step -Step 'install dir fully removed (polled up to 20s)' -Expected $true -Actual $appGone -Pass $appGone
} else {
    Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $uninst) { Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait }
}
