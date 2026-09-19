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
