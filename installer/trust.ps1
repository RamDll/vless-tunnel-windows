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

  Сертификат — самоподписанный (New-SelfSignedCertificate, 10 лет,
  CodeSigningCert), выпущен на стенде, приватный ключ (.pfx) роздан
  только в секреты GitHub Actions (SIGNING_PFX_BASE64/PASSWORD) и в
  офлайн-копию владельцу — в репозитории лежит только публичный
  installer/vless-tunnel.cer, как и положено (план, 3.8: ".pfx...
  никогда в репозитории").
#>
param(
    [string]$InstallerPath = (Join-Path $PSScriptRoot 'vless-tunnel-setup.exe'),
    [string]$CertPath = (Join-Path $PSScriptRoot 'vless-tunnel.cer')
)

# Зашитый отпечаток (план, 3.8: "сверяет отпечаток vless-tunnel.cer с
# зашитым в скрипт") — самоподписанный сертификат vless-tunnel, 10 лет,
# приватный ключ не хранится здесь и не хранится в репозитории вовсе.
$ExpectedThumbprint = '4E84442637C6083B440E6920B79DB36438544A1E'

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
