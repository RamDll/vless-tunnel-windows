# Стенд, п.2, пример: ACL на C:\ProgramData\vless-tunnel (ревью п.2 —
# SecureConfigDirectory) — раньше проверялся текстом icacls.exe,
# перегнанным через гостевую консоль -> SSH -> локальный grep, и ловил
# ложный FAIL на нестыковке локализованных подписей после перекодировок
# ("Отказано в доступе" не совпало по буквам). Здесь — только Get-Acl
# (System.Security.AccessControl), сравнение по SID (языконезависимые),
# без единого вызова icacls.exe и без сравнения строк.
#
# Функциональные строки (Step/Expected/Actual) — намеренно на английском:
# PowerShell 5.1 на Windows парсит .ps1 без BOM в системной ANSI-кодовой
# странице, а не в UTF-8 — кириллица внутри строковых литералов со
# вложенной интерполяцией $(...) при передаче через SCP+SSH ловила
# реальные ошибки разбора скрипта (не просто "кракозябры", а
# SyntaxError). Кириллица здесь — только в комментариях (не парсится).
#
# Дот-сорсится из run-test.ps1 — Add-Step уже в scope.

$dir = 'C:\ProgramData\vless-tunnel'
$usersSid = 'S-1-5-32-545'         # BUILTIN\Users — не должно быть вовсе
$systemSid = 'S-1-5-18'            # NT AUTHORITY\SYSTEM — обязателен, FullControl
$adminsSid = 'S-1-5-32-544'        # BUILTIN\Administrators — обязателен, FullControl

if (-not (Test-Path $dir)) {
    Add-Step -Step 'config dir exists' -Expected $dir -Actual 'missing' -Pass $false
    return
}

function Get-RuleSid($rule) {
    try { return $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
    catch { return $null }
}

$acl = Get-Acl -Path $dir
$rules = $acl.Access

Add-Step -Step 'inheritance disabled (isolated ACL)' `
    -Expected $true -Actual $acl.AreAccessRulesProtected -Pass $acl.AreAccessRulesProtected

$usersRule = $rules | Where-Object { (Get-RuleSid $_) -eq $usersSid }
$usersActual = if ($usersRule) { "present: $($usersRule.FileSystemRights)" } else { 'absent' }
Add-Step -Step 'no ACE for BUILTIN\Users' -Expected 'absent' -Actual $usersActual -Pass (-not $usersRule)

function Test-FullControlFor([string]$sid, [string]$label) {
    $rule = $rules | Where-Object { (Get-RuleSid $_) -eq $sid }
    $hasFullControl = $rule -and
        (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl) -and
        ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow)
    $actual = if ($rule) { "$($rule.FileSystemRights), $($rule.AccessControlType)" } else { 'absent' }
    Add-Step -Step "$label has FullControl" -Expected 'FullControl, Allow' -Actual $actual -Pass ([bool]$hasFullControl)
}

Test-FullControlFor $systemSid 'SYSTEM'
Test-FullControlFor $adminsSid 'Administrators'
