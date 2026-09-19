# Стенд, п.3 стиль / ревью п.16: DrainSubscriberAsync (события) и цикл
# запрос-ответ HandleClientAsync писали в ОДИН StreamWriter на одном
# соединении — ровно то, что делает трей (подписан и одновременно шлёт
# команды по тому же pipe). Тест: одно соединение подписывается и в
# цикле шлёт status, пока ВТОРОЙ (отдельный OS-процесс) быстро дёргает
# on/off, генерируя события StatusChanged — каждая полученная строка на
# первом соединении обязана быть валидным JSON (не смесью двух строк).
#
# host в ссылке подменяется на заведомо нерезолвящийся — Bootstrap-резолв
# проваливается за доли секунды (NXDOMAIN), без ожидания TUN (40с) —
# нужна только частая смена состояния (Starting -> Error), не настоящий
# туннель. Оригинальная ссылка восстанавливается в finally.
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
    Add-Step -Step 'build for ipc-writer-race' -Expected 0 -Actual $LASTEXITCODE -Pass $false -ExitCode $LASTEXITCODE
    return
}
Start-Process 'C:\dev\build\vless-tunnel-setup.exe' -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
Start-Sleep -Seconds 3
Start-Service -Name 'vless-tunnel' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

function Read-LineWithTimeout($reader, [int]$timeoutMs) {
    $task = $reader.ReadLineAsync()
    if ($task.Wait($timeoutMs)) { return $task.Result }
    return $null
}

$originalLink = (Get-Content $linkPath -Raw).Trim()
$badLink = $originalLink -replace '@[^:@]+:', '@invalid.invalid.example:'
$togglerProc = $null
$pipe = $null

try {
    $setResp = Invoke-VtIpc -Cmd 'set-link' -Link $badLink
    Add-Step -Step 'set-link (unresolvable host, for fast Error cycling) accepted' `
        -Expected $true -Actual ([bool]$setResp.Ok) -Pass ([bool]$setResp.Ok) -Info $setResp.Error

    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'vless-tunnel', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(5000)
    $reader = New-Object System.IO.StreamReader($pipe)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $writer.WriteLine('{"Cmd":"subscribe"}')
    $ack = Read-LineWithTimeout $reader 5000
    $ackOk = $false
    try { $ackOk = [bool]($ack | ConvertFrom-Json).Ok } catch { }
    Add-Step -Step 'subscribe ack valid JSON' -Expected $true -Actual $ackOk -Pass $ackOk

    # Отдельный OS-процесс (не runspace — надёжнее для реального
    # параллелизма из PowerShell), гоняет on/off по СВОЕМУ соединению,
    # генерируя события StatusChanged, пока первое соединение ниже шлёт
    # status по своему.
    $togglerScript = @'
for ($i = 0; $i -lt 60; $i++) {
    try {
        $p = New-Object System.IO.Pipes.NamedPipeClientStream(".", "vless-tunnel", [System.IO.Pipes.PipeDirection]::InOut)
        $p.Connect(3000)
        $w = New-Object System.IO.StreamWriter($p)
        $w.AutoFlush = $true
        $r = New-Object System.IO.StreamReader($p)
        $w.WriteLine('{"Cmd":"on"}')
        [void]$r.ReadLine()
        $w.WriteLine('{"Cmd":"off"}')
        [void]$r.ReadLine()
        $p.Dispose()
    } catch { }
}
'@
    $togglerFile = Join-Path $env:TEMP 'ipc-race-toggler.ps1'
    Set-Content -Path $togglerFile -Value $togglerScript -Encoding ASCII
    $togglerProc = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $togglerFile -PassThru -WindowStyle Hidden

    $badLines = New-Object System.Collections.Generic.List[string]
    $totalLines = 0
    for ($i = 0; $i -lt 150; $i++) {
        $writer.WriteLine('{"Cmd":"status"}')
        $line = Read-LineWithTimeout $reader 2000
        if ($null -eq $line) { continue }
        $totalLines++
        try { $null = $line | ConvertFrom-Json } catch { $badLines.Add($line) }
    }
    $togglerProc.WaitForExit(30000) | Out-Null

    # дочитать, что накопилось в очереди событий, не блокируясь навечно
    while ($true) {
        $line = Read-LineWithTimeout $reader 300
        if ($null -eq $line) { break }
        $totalLines++
        try { $null = $line | ConvertFrom-Json } catch { $badLines.Add($line) }
    }

    Add-Step -Step 'no corrupted NDJSON lines on shared subscribe+command connection' `
        -Expected 0 -Actual $badLines.Count -Pass ($badLines.Count -eq 0) -Info "totalLines=$totalLines"
} finally {
    if ($pipe) { $pipe.Dispose() }
    if ($togglerProc -and -not $togglerProc.HasExited) { $togglerProc | Stop-Process -Force -ErrorAction SilentlyContinue }
    Invoke-VtIpc -Cmd 'set-link' -Link $originalLink | Out-Null
    Invoke-VtIpc -Cmd 'off' | Out-Null
}
