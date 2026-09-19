# Стенд, п.3: сборка живёт целиком внутри гостя — dotnet publish (три
# self-contained проекта в одну dist-папку, как это делает CI) + ISCC
# (Inno Setup) без подписи (дев-сборка; подпись — только CI,
# .github/workflows/build.yml). Хосту не нужно гонять готовые бинарники
# туда-сюда через scp — только исходники (см. guest-run-scenario.ps1).
#
# xray.exe/wintun.dll кэшируются в C:\dev\build-cache — не тянутся заново
# на каждую сборку, только когда пин (vm\pinned-versions.txt) изменился
# или кэш ещё пуст; sha256 сверяется всегда, даже с кэша.

param(
    [string]$SrcDir = 'C:\dev\src',
    [string]$OutDir = 'C:\dev\build',
    [string]$Version = '0.0.0-dev'
)

$ErrorActionPreference = 'Stop'
$dotnet = 'C:\dev\dotnet\dotnet.exe'
$dist = Join-Path $OutDir 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Снести obj/bin ВСЕХ проектов (включая зависимости Core/Native, не
# только три верхних) перед публикацией — защита от инкрементальной
# сборки, которая теоретически может не пересобрать изменившийся файл,
# если git archive проставил ему mtime не новее, чем у уже собранных
# obj/bin с прошлого прогона на этом же госте. Живым тестом (ревью п.18)
# был пойман именно такой симптом (собранный .dll не содержал свежий
# фикс) — но настоящая причина в ТОМ прогоне оказалась куда прозаичнее:
# фикс просто не был закоммичен, а git archive HEAD (with-rescue.sh)
# берёт коммит, не рабочее дерево. Инкрементальность здесь ни при чём,
# но чистка всё равно остаётся дешёвой страховкой от неё на будущее.
# (dotnet publish не принимает --no-incremental — тот только для
# dotnet build; чистка obj/bin работает для обоих путей.)
foreach ($proj in 'Core', 'Native', 'Service', 'Cli', 'Tray') {
    $projDir = "$SrcDir\src\VlessTunnel.$proj"
    Remove-Item (Join-Path $projDir 'obj'), (Join-Path $projDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($proj in 'Service', 'Cli', 'Tray') {
    & $dotnet publish "$SrcDir\src\VlessTunnel.$proj\VlessTunnel.$proj.csproj" `
        -c Release -r win-x64 --self-contained true -o $dist "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish VlessTunnel.$proj failed with exit code $LASTEXITCODE" }
}

$pinned = Get-Content "$SrcDir\vm\pinned-versions.txt" -Raw
if ($pinned -notmatch '(?s)\[xray-core-windows\].*?url = (\S+)') { throw 'xray-core url not found in pinned-versions.txt' }
$xrayUrl = $matches[1]
if ($pinned -notmatch '(?s)\[xray-core-windows\].*?sha256 = (\w+)') { throw 'xray-core sha256 not found in pinned-versions.txt' }
$xraySha = $matches[1]

$cacheDir = 'C:\dev\build-cache'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$xrayZip = Join-Path $cacheDir 'xray.zip'
$haveGoodZip = (Test-Path $xrayZip) -and ((Get-FileHash $xrayZip -Algorithm SHA256).Hash.ToLower() -eq $xraySha)
if (-not $haveGoodZip) {
    Invoke-WebRequest -Uri $xrayUrl -OutFile $xrayZip -UseBasicParsing
    $actual = (Get-FileHash $xrayZip -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $xraySha) { throw "xray-core sha256 mismatch: expected $xraySha, got $actual" }
}
$xrayExtract = Join-Path $cacheDir 'xray-extracted'
if (-not (Test-Path (Join-Path $xrayExtract 'xray.exe'))) {
    Expand-Archive $xrayZip -DestinationPath $xrayExtract -Force
}
Copy-Item (Join-Path $xrayExtract 'xray.exe'), (Join-Path $xrayExtract 'wintun.dll') $dist -Force
Copy-Item "$SrcDir\packaging\icons\*.ico" $dist -Force

& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' "/DSourceDir=$dist" "/DMyAppVersion=$Version" "/O$OutDir" "$SrcDir\installer\vless-tunnel.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$setupExe = Join-Path $OutDir 'vless-tunnel-setup.exe'
if (-not (Test-Path $setupExe)) { throw "Expected Setup.exe not found at $setupExe" }
Write-Host "RESULT: BUILD OK -> $setupExe"
