param(
  [switch]$SkipTests,
  [switch]$IntegrationTests,   # VHD-based tests against the real storage stack (UAC prompt)
  [string]$TestRoot
)
# Reproducible build:
#   translation check -> core tests -> publish service/recovery/app -> MSI (de-DE + en-US)
#   -> embed English language transform -> Setup.exe with language selection -> SHA256SUMS.
# Requires the .NET 8 SDK. NuGet packages (Windows App SDK, WiX) are restored automatically.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'out'
$publish = Join-Path $out 'publish'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Run([string]$exe, [string[]]$arguments) {
  & $exe @arguments
  if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

# Version comes from Directory.Build.props (single source).
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$msiVersion = "$version.0"
# ProductCode: identical for both cultures, new for every version (deterministic from the version).
$md5 = [System.Security.Cryptography.MD5]::Create()
$productCode = ([Guid]::new($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes("HDD-Spindown-ProductCode-$version")))).ToString('B').ToUpperInvariant()
Write-Host "HDD Spindown $version · ProductCode $productCode"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $publish | Out-Null

Step 'Übersetzungen prüfen'
& (Join-Path $root 'tools\Check-Localization.ps1') -Root $root
if ($LASTEXITCODE -ne 0) { throw 'Übersetzungen unvollständig.' }

if (-not $SkipTests) {
  Step 'Kern-Tests (simuliert)'
  Run dotnet @('build', "$root\tests\HddSpindown.CoreTests", '-c', 'Release', '-v', 'q', '-nologo')
  if (-not $TestRoot) { $TestRoot = Join-Path $env:TEMP ('HDDSpindown-CoreTests-' + [Guid]::NewGuid().ToString('N')) }
  $tests = Get-ChildItem "$root\tests\HddSpindown.CoreTests\bin\Release" -Recurse -Filter CoreTests.exe | Select-Object -First 1
  Run $tests.FullName @($TestRoot)
}

if ($IntegrationTests) {
  Step 'Integrationstests mit Wegwerf-VHDs (Administrator)'
  Run dotnet @('build', "$root\tests\HddSpindown.IntegrationTests", '-c', 'Release', '-v', 'q', '-nologo')
  $it = Get-ChildItem "$root\tests\HddSpindown.IntegrationTests\bin\Release" -Recurse -Filter IntegrationTests.exe | Select-Object -First 1
  $p = Start-Process $it.FullName -Verb RunAs -PassThru -Wait
  $dir = Get-Content (Join-Path $env:TEMP 'HDDSpindown-IT-last.txt')
  Get-Content (Join-Path $dir 'integration.log') -Encoding UTF8 | Select-String 'Szenario|Prüfungen|ERGEBNIS|FEHL'
  if ($p.ExitCode -ne 0) { throw "Integrationstests fehlgeschlagen. Log: $dir\integration.log" }
}

Step 'Dienst und Recovery-Werkzeug veröffentlichen'
Run dotnet @('publish', "$root\src\HddSpindown.Service", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\Service", '-v', 'q', '-nologo')
Run dotnet @('publish', "$root\src\HddSpindown.Recovery", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\Service", '-v', 'q', '-nologo')

Step 'Oberfläche (WinUI 3) veröffentlichen'
Run dotnet @('publish', "$root\src\HddSpindown.App", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\App", '-v', 'q', '-nologo')

Step 'Installer (WiX, Deutsch + Englisch) bauen'
$msiBuild = Join-Path $out 'msi'
Run dotnet @('build', "$root\installer\HddSpindown.Installer.wixproj", '-c', 'Release', "-p:PublishRoot=$publish", "-p:ProductVersion=$msiVersion", "-p:ProductCode=$productCode", '-o', $msiBuild, '-v', 'q', '-nologo')
$msiDe = Get-ChildItem $msiBuild -Recurse -Filter *.msi | Where-Object { $_.FullName -match 'de-DE' } | Select-Object -First 1
$msiEn = Get-ChildItem $msiBuild -Recurse -Filter *.msi | Where-Object { $_.FullName -match 'en-US' } | Select-Object -First 1
if (-not $msiDe -or -not $msiEn) { throw 'MSI-Kulturen fehlen.' }
$msi = Join-Path $out "HDD-Spindown-$version-x64.msi"
& (Join-Path $root 'tools\Embed-LanguageTransform.ps1') -BaseMsi $msiDe.FullName -LanguageMsi $msiEn.FullName -OutMsi $msi -LanguageId 1033

Step 'Setup.exe mit Sprachauswahl bauen'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$info = Join-Path $out 'SetupInfo.g.cs'
Set-Content -Path $info -Encoding UTF8 -Value "static partial class SetupInfo { public const string Version=`"$msiVersion`"; }"
$setup = Join-Path $out "HDD-Spindown-$version-Setup.exe"
Run $csc @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
  "/win32icon:$root\assets\HDD-Spindown.ico", "/win32manifest:$root\setup\Setup.manifest",
  '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
  "/resource:$msi,HDD-Spindown.msi", "/out:$setup", "$root\setup\Setup.cs", $info)
Remove-Item $info

Step 'Prüfsummen'
$lines = foreach ($f in @($setup, $msi, "$publish\Service\HddSpindown.Service.exe", "$publish\Service\HddSpindown.Recovery.exe", "$publish\App\HDD Spindown.exe")) {
  $h = Get-FileHash -Algorithm SHA256 $f
  "$($h.Hash)  $($f.Substring($out.Length + 1))"
}
$lines | Set-Content -Encoding utf8 (Join-Path $out 'SHA256SUMS.txt')
$lines
Write-Host "`nFertig:" -ForegroundColor Green
Write-Host "  $setup ($([math]::Round((Get-Item $setup).Length/1MB,1)) MB)  – Sprachauswahl, empfohlen"
Write-Host "  $msi ($([math]::Round((Get-Item $msi).Length/1MB,1)) MB)  – direkt: Deutsch; mit TRANSFORMS=:1033 Englisch"
