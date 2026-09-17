# Первичная настройка Windows внутри vt-win10 (план, раздел 5.2).
# Запускается от SYSTEM синхронно из oobeSystem-прохода autounattend.xml,
# без интерактивного входа. В конце выключает машину — это сигнал хосту
# (create-vm.sh), что установка завершена.
#
# Параметр -ResourceDrive — буква диска (например "D:") с этим же ISO
# (ресурсным), где лежат vt-setup-guest.ps1, rescue.ps1, run-test.ps1 и
# authorized_key.pub. Сам virtio-win ISO — отдельный CD-ROM, его букву
# скрипт ищет сам (заранее не известна).

param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceDrive
)

$ErrorActionPreference = 'Continue'
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

Step 'SSH-ключ администратора' {
    $keyFile = "$ResourceDrive\authorized_key.pub"
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

Step 'Windows Update: пауза' {
    # Пауза через API нестабильна между билдами; проще и надёжнее для
    # одноразового тестового стенда — отключить службу целиком.
    New-Item -ItemType Directory -Force -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' | Out-Null
    Set-ItemProperty -Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' `
        -Name 'NoAutoRebootWithLoggedOnUsers' -Value 1 -Type DWord
    Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
    Set-Service wuauserv -StartupType Disabled -ErrorAction SilentlyContinue
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
    Copy-Item "$ResourceDrive\rescue.ps1" 'C:\dev\bin\rescue.ps1' -Force
    Copy-Item "$ResourceDrive\run-test.ps1" 'C:\dev\bin\run-test.ps1' -Force
}

Write-Host 'RESULT: SETUP-GUEST DONE'
Stop-Transcript
Stop-Computer -Force
