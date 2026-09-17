# Обёртка одного опасного теста внутри виртуалки (план, 5.5).
# Применить -> проверить -> записать RESULT: PASS/FAIL/ERROR -> откатить
# в finally через rescue.ps1, что бы ни случилось со $Script.

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Script
)

New-Item -ItemType Directory -Force -Path 'C:\dev\logs' | Out-Null
$log = "C:\dev\logs\$Name-$(Get-Date -Format yyyyMMdd-HHmmss).log"
Start-Transcript -Path $log
try {
    & $Script
} catch {
    Write-Host "RESULT: ERROR $_"
} finally {
    & 'C:\dev\bin\rescue.ps1'
    Stop-Transcript
}
