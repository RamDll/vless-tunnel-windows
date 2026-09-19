# Стенд, п.4, живой тест: намеренно ломаем машину (kill-switch поднят,
# служба убита без штатного off) и смотрим, вернёт ли with-rescue.sh
# стенд в рабочее состояние без ручного вмешательства. Сам сценарий не
# проверяет продукт — это тест ИНФРАСТРУКТУРЫ восстановления, не фикса.
#
# Дот-сорсится из run-test.ps1 — Add-Step уже в scope.

& 'C:\Program Files\vless-tunnel\VlessTunnel.Cli.exe' on | Out-Null
Start-Sleep -Seconds 3

$svc = Get-CimInstance Win32_Service -Filter "Name='vless-tunnel'"
if (-not $svc -or -not $svc.ProcessId) {
    Add-Step -Step 'service running before kill' -Expected 'has PID' -Actual 'no PID' -Pass $false
    return
}
Stop-Process -Id $svc.ProcessId -Force

Add-Step -Step 'service process killed while tunnel on (kill-switch left up)' `
    -Expected 'killed' -Actual 'killed' -Pass $true -Info 'stand recovery test, not a product check'
