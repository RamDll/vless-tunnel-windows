# Стенд, живой тест ревью п.23 (2/3): удаление при УЖЕ ОСТАНОВЛЕННОЙ
# службе — не должно падать (StopAndCleanupServiceForUninstall не
# предполагает, что служба обязательно жива).
#
# Раньше это был один файл на все 3 сценария п.23 (uninstall-with-
# tunnel-on.ps1) — разбито на 3 независимых файла, свой снимок/откат
# на каждый (см. uninstall-tunnel-on-silent.ps1 за подробностями,
# почему).
#
# Дот-сорсится из run-test.ps1 — Add-Step уже в scope.

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

$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
Add-Step -Step 'install exit code' -Expected 0 -Actual $p.ExitCode -Pass ($p.ExitCode -eq 0)

Stop-Service -Name 'vless-tunnel' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$svcState = (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue).Status
Add-Step -Step 'service was stopped before uninstall' -Expected 'Stopped' -Actual "$svcState" -Pass ($svcState -eq 'Stopped')

$uninstExit = (Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru).ExitCode
Start-Sleep -Seconds 2
Add-Step -Step 'uninstall with already-stopped service exit code' -Expected 0 -Actual $uninstExit -Pass ($uninstExit -eq 0)

$svcGone = -not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)
Add-Step -Step 'service unregistered' -Expected $true -Actual $svcGone -Pass $svcGone
