# Стенд, п.3: цикл on/off целиком внутри гостя, продукт опрашивается
# НАПРЯМУЮ по IPC (Invoke-VtIpc из run-test.ps1), не через текст
# VlessTunnel.Cli.exe. Маршруты — через Get-NetRoute/Get-NetAdapter
# (структурные данные), не через текст route print.
#
# Дот-сорсится из run-test.ps1 — Add-Step/Invoke-VtIpc уже в scope.

$ErrorActionPreference = 'Stop'

Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (-not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\dev\bin\guest-build.ps1' -Version '0.0.0-scenario'
    if ($LASTEXITCODE -ne 0) {
        Add-Step -Step 'build for on-off-cycle' -Expected 0 -Actual $LASTEXITCODE -Pass $false -ExitCode $LASTEXITCODE
        return
    }
    Start-Process 'C:\dev\build\vless-tunnel-setup.exe' -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    Start-Sleep -Seconds 3
}
Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (-not (Test-Path 'C:\ProgramData\vless-tunnel\link.txt')) {
    Add-Step -Step 'link configured (prerequisite)' -Expected 'present' -Actual 'absent' -Pass $false `
        -Info 'no persisted link.txt and no test-link.txt delivered by host — cannot exercise on/off'
    return
}

# ifIndex xray0 бывает переиспользован ОС между поднятиями, поэтому имя
# адаптера, не индекс, — надёжный признак "TUN сейчас поднят". Ревью
# п.21: имя чередуется между xray0/xray0b (лечит гонку пересоздания
# wintun-адаптера), поэтому проверяем оба варианта, не только "xray0".
function Get-XrayTunAdapter { Get-NetAdapter | Where-Object { $_.Name -eq 'xray0' -or $_.Name -eq 'xray0b' } }

$cycles = 3
for ($i = 1; $i -le $cycles; $i++) {
    $onResp = $null
    $attempts = 0
    do {
        $attempts++
        $onResp = Invoke-VtIpc -Cmd 'on'
    } while (-not $onResp.Ok -and $attempts -lt 3)

    Add-Step -Step "cycle ${i}: on succeeded (attempts=$attempts)" -Expected $true -Actual ([bool]$onResp.Ok) -Pass ([bool]$onResp.Ok) -Info $onResp.Error

    if ($onResp.Ok) {
        $tunIf = Get-XrayTunAdapter
        $serverHost = $onResp.Status.ServerHost
        $hostRoute = if ($serverHost) { Get-NetRoute -DestinationPrefix "$serverHost/32" -ErrorAction SilentlyContinue } else { $null }
        $hostRouteOk = $hostRoute -and $tunIf -and ($hostRoute.InterfaceIndex -ne $tunIf.InterfaceIndex)
        Add-Step -Step "cycle ${i}: host route present, not via TUN" -Expected 'present, non-TUN interface' `
            -Actual $(if ($hostRoute) { "ifIndex=$($hostRoute.InterfaceIndex), tunIfIndex=$($tunIf.InterfaceIndex)" } else { 'absent' }) `
            -Pass ([bool]$hostRouteOk)
    }

    $offResp = Invoke-VtIpc -Cmd 'off'
    Add-Step -Step "cycle ${i}: off succeeded" -Expected $true -Actual ([bool]$offResp.Ok) -Pass ([bool]$offResp.Ok) -Info $offResp.Error

    $leftoverTun = Get-XrayTunAdapter
    Add-Step -Step "cycle ${i}: TUN adapter gone after off" -Expected 'absent' `
        -Actual $(if ($leftoverTun) { 'present' } else { 'absent' }) -Pass (-not $leftoverTun)
}
