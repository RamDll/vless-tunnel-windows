# Первичная настройка Windows внутри vt-win10 (план, раздел 5.2).
# Запускается через FirstLogonCommands при автологине vtadmin (единственный
# рабочий механизм из проверенных — RunSynchronousCommand в specialize и в
# oobeSystem регистрировался, но не выполнялся; $OEM$\$1\ тоже не работает
# для установки без WDS/MDT — Setup копирует такую папку на C:\ только с
# самого install-ISO, не со второго CD-ROM). Файлы (rescue.ps1, run-test.ps1,
# authorized_key.pub, и сам этот скрипт) лежат в корне ресурсного ISO;
# -ResourceDrive — буква, под которой этот CD-ROM оказался на этот раз (её
# заранее не угадать, ищет вызывающий cmd-однострочник в autounattend.xml).
# В конце выключает машину — сигнал хосту (create-vm.sh), что всё готово.
#
# virtio-win ISO — отдельный CD-ROM, его букву ищем по факту тем же способом.

param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceDrive
)

$ErrorActionPreference = 'Continue'
$ToolsDir = $ResourceDrive
New-Item -ItemType Directory -Force -Path 'C:\dev\logs', 'C:\dev\secrets', 'C:\dev\bin' | Out-Null
Start-Transcript -Path 'C:\dev\logs\setup-guest.log' -Append

function Find-DriveWithFile {
    param([string]$RelativePath)
    foreach ($letter in 'D', 'E', 'F', 'G', 'H') {
        $candidate = "$letter`:\$RelativePath"
        if (Test-Path $candidate) { return "$letter`:" }
    }
    return $null
}

function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "=== $Name ==="
    try {
        & $Body
        Write-Host "RESULT: $Name OK"
    } catch {
        Write-Host "RESULT: $Name FAILED: $_"
    }
}

Step 'virtio-win guest agent' {
    $virtioDrive = Find-DriveWithFile 'guest-agent\qemu-ga-x86_64.msi'
    if (-not $virtioDrive) { throw 'virtio-win ISO not found on any drive D:..H:' }
    Start-Process msiexec.exe -ArgumentList "/i `"$virtioDrive\guest-agent\qemu-ga-x86_64.msi`" /qn /norestart" -Wait -NoNewWindow
    Set-Service -Name QEMU-GA -StartupType Automatic -ErrorAction SilentlyContinue
    Start-Service -Name QEMU-GA -ErrorAction SilentlyContinue
}

Step 'virtio drivers (best-effort, not fatal)' {
    $virtioDrive = Find-DriveWithFile 'virtio-win-guest-tools.exe'
    if ($virtioDrive) {
        Start-Process "$virtioDrive\virtio-win-guest-tools.exe" -ArgumentList '/install /quiet /norestart' -Wait -NoNewWindow
    }
}

Step 'OpenSSH Server' {
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0'
    if ($cap.State -ne 'Installed') {
        Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
    }
    Set-Service -Name sshd -StartupType Automatic
    Start-Service sshd
    if (-not (Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -DisplayName 'OpenSSH Server (sshd)' `
            -Enabled True -Direction Inbound -Protocol TCP -Action Allow -LocalPort 22 | Out-Null
    }
    New-Item -ItemType Directory -Force -Path 'HKLM:\SOFTWARE\OpenSSH' | Out-Null
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name 'DefaultShell' `
        -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
}

# Стенд, п.1: SSH — только по отдельному управляющему адаптеру (vt-mgmt,
# host-only libvirt-сеть, статический IP по DHCP-резервации в vm/create-vm.sh).
# Раньше SSH шёл по тому же адаптеру, что и сами сетевые тесты (kill-switch,
# дёрганье маршрутов, reboot) — то, что канал это переживал, было случайностью
# устройства WFP-фильтров продукта (только ALE_AUTH_CONNECT), не гарантией.
# Второй адаптер получает IP по DHCP не мгновенно — ждём его перед тем, как
# трогать sshd_config.
Step 'Управляющая сеть vt-mgmt: SSH только на ней' {
    $mgmtMacWindows = ('52:54:00:89:6c:86' -replace ':', '-').ToUpper()
    $mgmtIp = $null
    for ($i = 0; $i -lt 30 -and -not $mgmtIp; $i++) {
        $nic = Get-NetAdapter | Where-Object { $_.MacAddress -eq $mgmtMacWindows }
        if ($nic) {
            $addr = Get-NetIPAddress -InterfaceIndex $nic.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue
            if ($addr) { $mgmtIp = $addr.IPAddress; $mgmtAdapterName = $nic.Name }
        }
        if (-not $mgmtIp) { Start-Sleep -Seconds 2 }
    }
    if (-not $mgmtIp) { throw "vt-mgmt адаптер (MAC $mgmtMacWindows) не получил IP за отведённое время" }
    if ($mgmtAdapterName -ne 'vt-mgmt') { Rename-NetAdapter -Name $mgmtAdapterName -NewName 'vt-mgmt' }

    $sshdConfig = 'C:\ProgramData\ssh\sshd_config'
    $lines = Get-Content $sshdConfig
    $lines = $lines | Where-Object { $_ -notmatch '^\s*ListenAddress\s' }
    $insertAt = ($lines | Select-String -Pattern '^#ListenAddress ::' | Select-Object -First 1).LineNumber
    if ($insertAt) {
        $newLines = $lines[0..($insertAt - 1)] + "ListenAddress $mgmtIp" + $lines[$insertAt..($lines.Count - 1)]
    } else {
        $newLines = @("ListenAddress $mgmtIp") + $lines
    }
    Set-Content -Path $sshdConfig -Value $newLines -Encoding ASCII
    Restart-Service sshd
}

# Ревью п.22: третий адаптер для живых тестов смены сети (NAT, отдельная
# подсеть от default, см. vm/vt-test2-network.xml/create-vm.sh) — только
# переименование для консистентности, SSH его не касается вовсе.
Step 'Переименовать тестовый адаптер vt-test2' {
    $testMacWindows = ('52:54:00:89:6c:87' -replace ':', '-').ToUpper()
    $nic = $null
    for ($i = 0; $i -lt 30 -and -not $nic; $i++) {
        $nic = Get-NetAdapter | Where-Object { $_.MacAddress -eq $testMacWindows }
        if (-not $nic) { Start-Sleep -Seconds 2 }
    }
    if (-not $nic) { throw "vt-test2 адаптер (MAC $testMacWindows) не появился за отведённое время" }
    if ($nic.Name -ne 'vt-test2') { Rename-NetAdapter -Name $nic.Name -NewName 'vt-test2' }
}

Step 'SSH-ключ администратора' {
    $keyFile = "$ToolsDir\authorized_key.pub"
    if (-not (Test-Path $keyFile)) { throw "authorized_key.pub not found at $keyFile" }
    New-Item -ItemType Directory -Force -Path 'C:\ProgramData\ssh' | Out-Null
    $dest = 'C:\ProgramData\ssh\administrators_authorized_keys'
    Copy-Item $keyFile $dest -Force
    icacls $dest /inheritance:r | Out-Null
    icacls $dest /grant 'SYSTEM:F' | Out-Null
    icacls $dest /grant 'Administrators:F' | Out-Null
}

Step 'Питание: без сна/гибернации' {
    powercfg /change standby-timeout-ac 0
    powercfg /change standby-timeout-dc 0
    powercfg /change monitor-timeout-ac 0
    powercfg /hibernate off
}

Step 'Defender: исключить C:\dev' {
    $ok = $false
    for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
        try {
            Add-MpPreference -ExclusionPath 'C:\dev' -ErrorAction Stop
            $ok = $true
        } catch {
            Start-Sleep -Seconds 5
        }
    }
    if (-not $ok) { throw 'Add-MpPreference did not succeed after retries' }
}

Step 'Копирование rescue.ps1 / run-test.ps1 в C:\dev\bin' {
    Copy-Item "$ToolsDir\rescue.ps1" 'C:\dev\bin\rescue.ps1' -Force
    Copy-Item "$ToolsDir\run-test.ps1" 'C:\dev\bin\run-test.ps1' -Force
}

# Стенд, п.3: раннер сценариев (build+install+тест) живёт целиком внутри
# гостя (vm/guest-build.ps1, vm/guest-run-scenario.ps1) — нужен свой
# .NET SDK и Inno Setup, не только то, что ставит сам продукт.
Step '.NET SDK для сборки внутри гостя' {
    if (Test-Path 'C:\dev\dotnet\dotnet.exe') { return }
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile 'C:\dev\bin\dotnet-install.ps1' -UseBasicParsing
    & powershell -ExecutionPolicy Bypass -NoProfile -File 'C:\dev\bin\dotnet-install.ps1' -Channel 10.0 -InstallDir 'C:\dev\dotnet'
}

Step 'Inno Setup для сборки установщика внутри гостя' {
    if (Test-Path 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') { return }
    $isSetup = Join-Path $env:TEMP 'innosetup-install.exe'
    Invoke-WebRequest -Uri 'https://files.jrsoftware.org/is/6/innosetup-6.7.3.exe' -OutFile $isSetup -UseBasicParsing
    Start-Process $isSetup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
}

Write-Host 'RESULT: SETUP-GUEST DONE'
Stop-Transcript
Stop-Computer -Force
