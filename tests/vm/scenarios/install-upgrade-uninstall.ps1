# Стенд, п.3: install/upgrade/uninstall целиком внутри гостя — сборка
# (guest-build.ps1), тихая установка, тихая переустановка поверх (тот же
# путь кода, что ServiceExists=True в vless-tunnel.iss), тихое удаление.
# Все проверки — через Get-Service/Test-Path/Get-ItemProperty (структурные
# данные), ни разу не через текст вывода Setup.exe/unins000.exe.
#
# Дот-сорсится из run-test.ps1 — Add-Step уже в scope.

$ErrorActionPreference = 'Stop'
$installDir = 'C:\Program Files\vless-tunnel'

# Трей, оставшийся от предыдущего интерактивного сеанса, держит открытым
# общий рантайм-DLL (Accessibility.dll и т.п.) в ДРУГОЙ сессии Windows —
# RestartManager Inno Setup не умеет перезапускать процессы из чужой
# сессии (не тот случай, что реальный апгрейд из-под самого трея, тот
# уже закрыт отдельно в SelfUpdater.CloseRunningTray), Setup.exe тогда
# откатывается с Abort. Гигиена стенда, не тест продукта — закрываем
# заранее, а не полагаемся на RestartManager через границу сессий.
Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\dev\bin\guest-build.ps1' -Version '0.0.0-scenario'
if ($LASTEXITCODE -ne 0) {
    Add-Step -Step 'build produced Setup.exe' -Expected 0 -Actual $LASTEXITCODE -Pass $false -ExitCode $LASTEXITCODE
    return
}
$setup = 'C:\dev\build\vless-tunnel-setup.exe'
Add-Step -Step 'Setup.exe exists after build' -Expected $true -Actual (Test-Path $setup) -Pass (Test-Path $setup)

# --- уборка перед стартом: если уже установлено (прошлые прогоны) -- снести ---
$uninst = Join-Path $installDir 'unins000.exe'
if (Test-Path $uninst) {
    Start-Process $uninst -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Seconds 2
}

function Get-ServiceState {
    Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
}

# --- 1. чистая установка ---
$p = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
$svc = Get-ServiceState
Add-Step -Step 'clean install: Setup.exe exit code' -Expected 0 -Actual $p.ExitCode -Pass ($p.ExitCode -eq 0) -ExitCode $p.ExitCode
Add-Step -Step 'clean install: service registered' -Expected 'exists' -Actual $(if ($svc) { 'exists' } else { 'missing' }) -Pass ([bool]$svc)
Add-Step -Step 'clean install: service running' -Expected 'Running' -Actual $(if ($svc) { $svc.Status.ToString() } else { 'n/a' }) -Pass ($svc -and $svc.Status -eq 'Running')

# --- 2. переустановка поверх (тот же путь кода, что апгрейд: ServiceExists=True в .iss) ---
$p2 = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
Start-Sleep -Seconds 3
$svc2 = Get-ServiceState
Add-Step -Step 'reinstall over existing: Setup.exe exit code' -Expected 0 -Actual $p2.ExitCode -Pass ($p2.ExitCode -eq 0) -ExitCode $p2.ExitCode
Add-Step -Step 'reinstall over existing: service still registered' -Expected 'exists' -Actual $(if ($svc2) { 'exists' } else { 'missing' }) -Pass ([bool]$svc2)
Add-Step -Step 'reinstall over existing: service running' -Expected 'Running' -Actual $(if ($svc2) { $svc2.Status.ToString() } else { 'n/a' }) -Pass ($svc2 -and $svc2.Status -eq 'Running')

# --- 3. удаление ---
$uninst2 = Join-Path $installDir 'unins000.exe'
$uninstExists = Test-Path $uninst2
Add-Step -Step 'uninstaller present before removal' -Expected $true -Actual $uninstExists -Pass $uninstExists
if ($uninstExists) {
    $p3 = Start-Process $uninst2 -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait -PassThru
    Start-Sleep -Seconds 3
    $svc3 = Get-ServiceState
    Add-Step -Step 'uninstall: exit code' -Expected 0 -Actual $p3.ExitCode -Pass ($p3.ExitCode -eq 0) -ExitCode $p3.ExitCode
    Add-Step -Step 'uninstall: service removed' -Expected 'missing' -Actual $(if ($svc3) { 'exists' } else { 'missing' }) -Pass (-not $svc3)
}
