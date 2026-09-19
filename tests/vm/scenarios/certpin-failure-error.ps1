# Стенд, п.3 стиль / ревью п.18: certPin == null (сервер недоступен в
# момент on) раньше всё равно поднимал туннель молча — только строка в
# логе, недоступная пользователю. Проверка: allowInsecure=1 на заведомо
# недоступный TLS-порт -> on должен дать Error с внятным текстом, не Ok.
#
# Дот-сорсится из run-test.ps1 — Add-Step/Invoke-VtIpc уже в scope.

$ErrorActionPreference = 'Stop'
$linkPath = 'C:\ProgramData\vless-tunnel\link.txt'

if (-not (Test-Path $linkPath)) {
    Add-Step -Step 'original link present (prerequisite)' -Expected 'present' -Actual 'absent' -Pass $false
    return
}
Get-Process -Name 'VlessTunnel.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

& powershell -NoProfile -ExecutionPolicy Bypass -File 'C:\dev\bin\guest-build.ps1' -Version '0.0.0-scenario'
if ($LASTEXITCODE -ne 0) {
    Add-Step -Step 'build for certpin-failure-error' -Expected 0 -Actual $LASTEXITCODE -Pass $false -ExitCode $LASTEXITCODE
    return
}
Start-Process 'C:\dev\build\vless-tunnel-setup.exe' -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
Start-Sleep -Seconds 3
Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$originalLink = (Get-Content $linkPath -Raw).Trim()
try {
    # security=tls (не reality) + allowInsecure=1 на закрытый порт
    # localhost — ECONNREFUSED почти мгновенно (быстрее, чем ждать
    # таймаут на недостижимый хост), CertPinResolver не получит
    # сертификат ни за какое время. host:port и security берутся из
    # реальной ссылки через regex на её собственных полях — собирать
    # vless:// с нуля вручную рискованнее угадать формат правильно.
    #
    # ВАЖНО (найдено живым тестом при отладке этого же сценария):
    # allowInsecure=1 нельзя просто дописать в конец строки — если у
    # ссылки есть #fragment (человекочитаемое имя после решётки, как у
    # реальной тестовой ссылки), всё, что допишешь ПОСЛЕ решётки, попадёт
    # во fragment и парсером как query-параметр не считается вовсе —
    # тогда AllowInsecure=false, туннель поднимается ПО-НАСТОЯЩЕМУ на
    # 127.0.0.1:1 (kill-switch включается независимо от того, реален ли
    # адрес назначения) и блокирует интернет гостю до штатного off,
    # который в такой ситуации даже не запрашивается. Добавляем ПЕРЕД
    # решёткой (или в конец, если решётки нет вовсе).
    $badLink = $originalLink -replace '@[^:@]+:\d+', '@127.0.0.1:1' -replace 'security=[a-z]+', 'security=tls'
    if ($badLink -notmatch 'allowInsecure=') {
        if ($badLink -match '^(?<base>[^#]*)(?<frag>#.*)?$') {
            $badLink = $Matches.base + '&allowInsecure=1' + $Matches.frag
        } else {
            $badLink += '&allowInsecure=1'
        }
    }

    $setResp = Invoke-VtIpc -Cmd 'set-link' -Link $badLink
    Add-Step -Step 'set-link (tls+allowInsecure, unreachable) accepted' `
        -Expected $true -Actual ([bool]$setResp.Ok) -Pass ([bool]$setResp.Ok) -Info $setResp.Error

    $onResp = Invoke-VtIpc -Cmd 'on' -TimeoutMs 30000
    Add-Step -Step 'on: Ok is false (certPin failure must not silently start tunnel)' `
        -Expected $false -Actual ([bool]$onResp.Ok) -Pass (-not [bool]$onResp.Ok) -Info $onResp.Error

    $statusResp = Invoke-VtIpc -Cmd 'status'
    $stateActual = "$($statusResp.Status.State)"
    Add-Step -Step 'status: State is Error (not On, not silently Off)' `
        -Expected 'Error' -Actual $stateActual -Pass ($stateActual -eq 'Error')

    $errActual = "$($statusResp.Status.Error)"
    Add-Step -Step 'status: Error text present and non-empty' `
        -Expected 'non-empty' -Actual $(if ($errActual) { 'present' } else { 'empty' }) -Pass ([bool]$errActual)
} finally {
    Invoke-VtIpc -Cmd 'set-link' -Link $originalLink | Out-Null
    Invoke-VtIpc -Cmd 'off' | Out-Null
}
