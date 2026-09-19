# Стенд, п.3: doctor целиком внутри гостя, напрямую по IPC
# (Invoke-VtIpc из run-test.ps1) — не через текст VlessTunnel.Cli.exe.
#
# Дот-сорсится из run-test.ps1 — Add-Step/Invoke-VtIpc уже в scope.

$ErrorActionPreference = 'Stop'

if (-not (Get-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue)) {
    Add-Step -Step 'service present (prerequisite)' -Expected 'exists' -Actual 'missing' -Pass $false
    return
}
Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$resp = Invoke-VtIpc -Cmd 'doctor'
Add-Step -Step 'doctor: Ok' -Expected $true -Actual ([bool]$resp.Ok) -Pass ([bool]$resp.Ok) -Info $resp.Error

if ($resp.Ok) {
    Add-Step -Step 'doctor: DoctorRemoved is present' -Expected 'not null' -Actual $resp.DoctorRemoved -Pass ($null -ne $resp.DoctorRemoved)

    $clockActual = if ($null -ne $resp.DoctorClockSkewSeconds) { "skew=$($resp.DoctorClockSkewSeconds)" }
        elseif ($resp.DoctorClockError) { "error=$($resp.DoctorClockError)" }
        else { 'neither' }
    Add-Step -Step 'doctor: clock check reported (skew or explicit error, not silently neither)' `
        -Expected 'one of the two present' -Actual $clockActual `
        -Pass (($null -ne $resp.DoctorClockSkewSeconds) -or [bool]$resp.DoctorClockError)
}
