# Read-only ABI/inventory comparison; no queue, package or service is changed.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$root=Split-Path -Parent $PSScriptRoot
Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $root 'src\Isafom.Windows\Scripts\DriverLocks.cs.txt') -Raw)
$native=@([IsafomDriverLocks]::ReferencedFiles())
if($native.Count -eq 0){throw 'NativeInventoryEmpty'}
$names=@($native|ForEach-Object {[IO.Path]::GetFileName($_)})
$expected=@(foreach($driver in @(Get-PrinterDriver)){
 foreach($field in @('DriverPath','ConfigFile','DataFile','HelpFile','DependentFiles')){
  if($null -eq $driver.PSObject.Properties[$field]){continue}
  foreach($path in $driver.$field){if($path){[IO.Path]::GetFileName($path)}}
 }
})
if($expected.Count -eq 0 -or @($expected|Where-Object {$_ -notin $names}).Count){throw 'NativeInventoryMissedDriverReference'}
Write-Output 'RESULT 2 native driver inventory checks passed in Windows PowerShell; registered driver file references agree with CIM.'
