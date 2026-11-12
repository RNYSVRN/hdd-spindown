param(
  [Parameter(Mandatory)][string]$BaseMsi,      # German build (becomes the result)
  [Parameter(Mandatory)][string]$LanguageMsi,  # English build
  [Parameter(Mandatory)][string]$OutMsi,
  [int]$LanguageId = 1033
)
# Creates a language transform (English vs. German) and embeds it as storage
# ":1033" into a copy of the German MSI. msiexec TRANSFORMS=:1033 applies it.
$ErrorActionPreference = 'Stop'
# COM needs plain strings; paths from FileInfo.FullName arrive wrapped as PSObject.
$BaseMsi = [string]$BaseMsi; $LanguageMsi = [string]$LanguageMsi; $OutMsi = [IO.Path]::GetFullPath([string]$OutMsi)
$work = Join-Path ([IO.Path]::GetTempPath()) ('hdds-mst-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
  Copy-Item $BaseMsi $OutMsi -Force
  $mst = [string](Join-Path $work "$LanguageId.mst")
  $installer = New-Object -ComObject WindowsInstaller.Installer
  $base = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($OutMsi, 0))       # read-only
  $lang = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($LanguageMsi, 0))
  [void]$lang.GetType().InvokeMember('GenerateTransform', 'InvokeMethod', $null, $lang, @($base, $mst))
  # Suppress all apply errors and validation, as language transforms are expected to differ.
  [void]$lang.GetType().InvokeMember('CreateTransformSummaryInfo', 'InvokeMethod', $null, $lang, @($base, $mst, 0, 0))
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($lang) | Out-Null
  [System.Runtime.InteropServices.Marshal]::ReleaseComObject($base) | Out-Null

  $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($OutMsi, 1))         # transact
  $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT `Name`,`Data` FROM `_Storages`'))
  [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
  $record = $installer.GetType().InvokeMember('CreateRecord', 'InvokeMethod', $null, $installer, @(2))
  [void]$record.GetType().InvokeMember('StringData', 'SetProperty', $null, $record, @(1, "$LanguageId"))
  [void]$record.GetType().InvokeMember('SetStream', 'InvokeMethod', $null, $record, @(2, $mst))
  [void]$view.GetType().InvokeMember('Modify', 'InvokeMethod', $null, $view, @(3, $record))   # msiViewModifyAssign
  [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)

  # Template summary: platform;languages – lists both so the package advertises them.
  $si = $db.GetType().InvokeMember('SummaryInformation', 'GetProperty', $null, $db, @(1))
  [void]$si.GetType().InvokeMember('Property', 'SetProperty', $null, $si, @(7, "x64;1031,$LanguageId"))
  [void]$si.GetType().InvokeMember('Persist', 'InvokeMethod', $null, $si, $null)
  [void]$db.GetType().InvokeMember('Commit', 'InvokeMethod', $null, $db, $null)
  foreach($o in @($si,$record,$view,$db,$installer)) { [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) | Out-Null }
  [GC]::Collect(); [GC]::WaitForPendingFinalizers()
  "Sprach-Transform :$LanguageId eingebettet: $OutMsi"
} finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
