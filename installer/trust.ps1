#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Разовый шаг доверия перед первой установкой vless-tunnel (план, 3.8, п.1).

.DESCRIPTION
  Сверяет отпечаток C:\...\vless-tunnel.cer (рядом со скриптом или с
  установщиком) с тем, что зашит ниже в $ExpectedThumbprint — только
  после совпадения ставит сертификат в LocalMachine\Root и
  LocalMachine\TrustedPublisher, снимает метку "из интернета"
  (Unblock-File) с установщика и запускает его. Повторно не нужен —
  установщик умеет обновляться поверх без trust.ps1 (план, 3.8, п.2).

  НЕ запускался живьём в этой сессии (нет ни реального сертификата, ни
  собранного установщика) — написан по плану, порядок шагов и проверка
  отпечатка соответствуют требованию "сверяет отпечаток... зашитый в
  скрипт", но нуждается в живой проверке вместе с installer/vless-tunnel.iss
  на стенде (план, этап 6).
#>
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'vless-tunnel-setup.exe'),
    [string]$CertPath = (Join-Path $PSScriptRoot 'vless-tunnel.cer')
)

# Зашитый отпечаток (план, 3.8: "сверяет отпечаток vless-tunnel.cer с
# зашитым в скрипт") — пустая строка означает "сертификат ещё не выпущен",
# скрипт тогда честно отказывается работать, а не пропускает проверку.
$ExpectedThumbprint = ''

function Fail($message) {
    Write-Error $message
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ExpectedThumbprint)) {
    Fail 'trust.ps1: $ExpectedThumbprint ещё не заполнен (сертификат ещё не выпущен) — см. installer/vless-tunnel.iss и план, 3.8.'
}

if (-not (Test-Path $CertPath)) {
    Fail "Не найден сертификат: $CertPath"
}
if (-not (Test-Path $InstallerPath)) {
    Fail "Не найден установщик: $InstallerPath"
}

$cert = Get-PfxCertificate -FilePath $CertPath
if ($cert.Thumbprint -ne $ExpectedThumbprint) {
    Fail "Отпечаток $CertPath ($($cert.Thumbprint)) не совпадает с ожидаемым ($ExpectedThumbprint) — устанавливать НЕ безопасно."
}

Write-Host "Отпечаток сертификата подтверждён: $($cert.Thumbprint)"

foreach ($store in @('Root', 'TrustedPublisher')) {
    Write-Host "Добавляю сертификат в LocalMachine\$store..."
    Import-Certificate -FilePath $CertPath -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
}

Write-Host "Снимаю метку «из интернета» с установщика..."
Unblock-File -Path $InstallerPath

Write-Host "Запускаю установщик..."
Start-Process -FilePath $InstallerPath -Wait
