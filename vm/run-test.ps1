# Обёртка одного опасного теста внутри виртуалки (план, 5.5).
# Стенд, п.2: раньше результат уходил только текстом в Transcript, который
# едет через гостевую консоль -> SSH -> локальный grep несколько
# перекодировок подряд — на этом поймали ложный FAIL (сравнение
# локализованного текста "Отказано в доступе" не совпало по буквам).
# Теперь сценарий пишет результат СТРУКТУРИРОВАННО через Add-Step (см.
# ниже) в JSON-файл (UTF-8 без BOM), с кодами ошибок ЧИСЛАМИ — хост читает
# файл и сам считает вердикт, не сверяя байты локализованного текста.
# Локализованный текст (если нужен для человека) идёт только в свободное
# поле Info, вердикт по нему не строится.
#
# $Script дот-сорсится (. $Script, не & $Script) — сценарий выполняется в
# ЭТОМ ЖЕ scope и может звать Add-Step напрямую.

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Script
)

New-Item -ItemType Directory -Force -Path 'C:\dev\logs' | Out-Null
$stamp = Get-Date -Format yyyyMMdd-HHmmss
$log = "C:\dev\logs\$Name-$stamp.log"
$reportPath = "C:\dev\logs\$Name-$stamp.json"

$script:Steps = [System.Collections.Generic.List[object]]::new()

# Один шаг проверки. $Pass — единственное, что решает PASS/FAIL для этого
# шага, и должно исходить из ЧИСЕЛ/структурных данных (SID, Win32-код,
# HRESULT, exit code), не из сравнения локализованных строк. $Info —
# опционально, для человека, в вердикт не участвует.
function Add-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Step,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)][bool]$Pass,
        [Nullable[int]]$Win32Error = $null,
        [Nullable[int]]$HResult = $null,
        [Nullable[int]]$ExitCode = $null,
        [string]$Info = $null
    )
    $script:Steps.Add([ordered]@{
        step        = $Step
        expected    = $Expected
        actual      = $Actual
        verdict     = if ($Pass) { 'PASS' } else { 'FAIL' }
        win32_error = $Win32Error
        hresult     = $HResult
        exit_code   = $ExitCode
        info        = $Info
    })
}

# Стенд, п.3: сценарии, которые говорят с самой службой (on/off/doctor/
# status), делают это НАПРЯМУЮ по именованному каналу (протокол —
# VlessTunnel.Core.Ipc.IpcCommands: NDJSON, {"Cmd":...}/{"Ok":...}), а не
# через VlessTunnel.Cli.exe — тот форматирует ответ в текст для человека
# (Console.WriteLine), а сверять вердикт нужно по структуре, не по тексту.
function Invoke-VtIpc {
    param(
        [Parameter(Mandatory = $true)][string]$Cmd,
        [string]$Link = $null,
        [int]$TimeoutMs = 90000
    )
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'vless-tunnel', [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(5000)
        $writer = New-Object System.IO.StreamWriter($pipe)
        $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)
        $req = [ordered]@{ Cmd = $Cmd }
        if ($Link) { $req.Link = $Link }
        $writer.WriteLine(($req | ConvertTo-Json -Compress))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait($TimeoutMs)) { throw "IPC timeout after ${TimeoutMs}ms for Cmd=$Cmd" }
        return $task.Result | ConvertFrom-Json
    } finally {
        $pipe.Dispose()
    }
}

Start-Transcript -Path $log
$overall = 'ERROR'
$scriptError = $null
try {
    . $Script
    if ($script:Steps.Count -eq 0) {
        $overall = 'ERROR'
        $scriptError = 'сценарий не вызвал ни одного Add-Step'
    } elseif ($script:Steps | Where-Object { $_.verdict -ne 'PASS' }) {
        $overall = 'FAIL'
    } else {
        $overall = 'PASS'
    }
} catch {
    $overall = 'ERROR'
    $scriptError = $_.Exception.Message
    Write-Host "RESULT: ERROR $_"
} finally {
    & 'C:\dev\bin\rescue.ps1'
    Stop-Transcript
}

$report = [ordered]@{
    name         = $Name
    finished_utc = (Get-Date).ToUniversalTime().ToString('o')
    verdict      = $overall
    script_error = $scriptError
    steps        = $script:Steps
}
$json = $report | ConvertTo-Json -Depth 8
# UTF-8 БЕЗ BOM — явный кодек, не Out-File/Set-Content (те на Windows
# PowerShell 5.1 пишут UTF-8 С BOM по умолчанию, а с BOM некоторые
# JSON-парсеры на стороне хоста спотыкаются).
[IO.File]::WriteAllText($reportPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "RESULT: $overall (report: $reportPath)"
