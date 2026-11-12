param([string]$Root = (Split-Path $PSScriptRoot -Parent))
# Every German key used via L.T / L.F / L.Tok (and Say in the recovery tool)
# must have an English entry in src\HddSpindown.Core\Localization*.cs.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$catalog = New-Object 'System.Collections.Generic.HashSet[string]'
Get-ChildItem "$Root\src\HddSpindown.Core" -Filter 'Localization*.cs' | ForEach-Object {
  foreach ($m in [regex]::Matches([IO.File]::ReadAllText($_.FullName, $utf8), '\["((?:[^"\\]|\\.)*)"\]=')) { [void]$catalog.Add($m.Groups[1].Value) }
}
$missing = New-Object 'System.Collections.Generic.SortedSet[string]'
Get-ChildItem "$Root\src" -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notlike 'Localization*.cs' } | ForEach-Object {
  $c = [IO.File]::ReadAllText($_.FullName, $utf8)
  foreach ($m in [regex]::Matches($c, '(?:\bL\.(?:T|F|Tok)|\bSay)\("((?:[^"\\]|\\.)+)"')) {
    $k = $m.Groups[1].Value
    if ($k.Trim().Length -le 1) { continue }
    if (-not $catalog.Contains($k)) { [void]$missing.Add("$($_.Name): $k") }
  }
}
if ($missing.Count -gt 0) { $missing | ForEach-Object { Write-Host "Fehlende Übersetzung: $_" -ForegroundColor Yellow }; exit 1 }
Write-Host "Übersetzungen vollständig ($($catalog.Count) Einträge)."
exit 0
