#requires -Version 7.0
[CmdletBinding()]param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$lab=Join-Path $project 'research\local-lab'
$stage='Privileges';$changed=$false;$otherUnchanged=$null;$defaultUnchanged=$null
try {
 $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
 if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'AdministratorRequired'}
 $state=Get-Content -LiteralPath (Join-Path $lab 'queue-state.json') -Raw|ConvertFrom-Json
 $q=Get-Printer -Name $state.QueueName
 if($q.Name -cne ('ISAFOM_TEST_KX_'+$state.Token.Substring(0,8)) -or $q.Comment -cne ('ISAFOM KX SENTINEL 0.1.5 '+$state.Token) -or $q.PortName -cne 'FILE:' -or $q.Shared -or -not $q.PrinterStatus.ToString().Contains('Paused') -or @(Get-PrintJob -PrinterName $q.Name).Count -ne 0){throw 'OwnedPausedLabRequired'}
 $before=Get-Printer|Where-Object Name -ne $q.Name|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
 $defaultBefore=(Get-CimInstance Win32_Printer -Filter 'Default=TRUE').Name
 $stage='VerifyOriginalInf'
 $expected='B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7'
 $stored=@(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'System32\DriverStore\FileRepository') -Directory -Filter 'oemsetup.inf_amd64_*'|ForEach-Object {
  $file=Join-Path $_.FullName 'OEMSETUP.INF'
  if((Test-Path -LiteralPath $file) -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ceq $expected){$file}
 })
 if($stored.Count -ne 1 -or (Get-AuthenticodeSignature -LiteralPath (Join-Path (Split-Path -Parent $stored[0]) 'kyomita.cat')).Status -ne 'Valid'){throw 'SignedOriginalPackageRequired'}
 $driver='Kyocera TASKalfa 3554ci KX'
 $stage='RegisterGuideDriver'
 if(-not(Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)){Add-PrinterDriver -Name $driver -InfPath $stored[0]}
 $stage='SwitchOwnedFileQueue'
 Set-Printer -Name $q.Name -DriverName $driver
 $fresh=Get-Printer -Name $q.Name
 if($fresh.DriverName -cne $driver -or $fresh.PortName -cne 'FILE:' -or -not $fresh.PrinterStatus.ToString().Contains('Paused')){throw 'LabStateUnexpected'}
 $state.Driver=$driver
 $state|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $lab 'queue-state.json') -Encoding utf8
 $changed=$true
 $after=Get-Printer|Where-Object Name -ne $q.Name|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
 $otherUnchanged=$before -ceq $after
 $defaultUnchanged=$defaultBefore -ceq (Get-CimInstance Win32_Printer -Filter 'Default=TRUE').Name
 $stage='Completed'
}catch{}
[ordered]@{Completed=($stage -eq 'Completed');Stage=$stage;LabDriverUpdated=$changed;OtherPrinterMetadataUnchanged=$otherUnchanged;DefaultUnchanged=$defaultUnchanged;NoDownload=$true;PhysicalPrintPerformed=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $lab 'guide-driver-result.json') -Encoding utf8
