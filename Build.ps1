param(
  [switch]$SkipTests,
  [string]$TestRoot
)
# Reproducible build: core tests -> publish service, recovery and app -> MSI -> SHA256SUMS.
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

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $publish | Out-Null

if (-not $SkipTests) {
  Step 'Kern-Tests (simuliert)'
  Run dotnet @('build', "$root\tests\HddSpindown.CoreTests", '-c', 'Release', '-v', 'q', '-nologo')
  if (-not $TestRoot) { $TestRoot = Join-Path $env:TEMP ('HDDSpindown-CoreTests-' + [Guid]::NewGuid().ToString('N')) }
  $tests = Get-ChildItem "$root\tests\HddSpindown.CoreTests\bin\Release" -Recurse -Filter CoreTests.exe | Select-Object -First 1
  Run $tests.FullName @($TestRoot)
}

Step 'Dienst und Recovery-Werkzeug veröffentlichen'
Run dotnet @('publish', "$root\src\HddSpindown.Service", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\Service", '-v', 'q', '-nologo')
Run dotnet @('publish', "$root\src\HddSpindown.Recovery", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\Service", '-v', 'q', '-nologo')

Step 'Oberfläche (WinUI 3) veröffentlichen'
Run dotnet @('publish', "$root\src\HddSpindown.App", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', "$publish\App", '-v', 'q', '-nologo')

Step 'Installer (WiX) bauen'
Run dotnet @('build', "$root\installer\HddSpindown.Installer.wixproj", '-c', 'Release', "-p:PublishRoot=$publish", '-o', $out, '-v', 'q', '-nologo')

Step 'Prüfsummen'
$msi = Get-ChildItem $out -Filter '*.msi' | Select-Object -First 1
$lines = foreach ($f in @($msi.FullName, "$publish\Service\HddSpindown.Service.exe", "$publish\Service\HddSpindown.Recovery.exe", "$publish\App\HDD Spindown.exe")) {
  $h = Get-FileHash -Algorithm SHA256 $f
  "$($h.Hash)  $($f.Substring($out.Length + 1))"
}
$lines | Set-Content -Encoding utf8 (Join-Path $out 'SHA256SUMS.txt')
$lines
Write-Host "`nFertig: $($msi.FullName) ($([math]::Round($msi.Length/1MB,1)) MB)" -ForegroundColor Green
