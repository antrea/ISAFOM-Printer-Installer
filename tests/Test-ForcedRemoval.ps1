#requires -Version 5.1
param([string]$ProjectRoot='')
if(-not $ProjectRoot){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
. (Join-Path $ProjectRoot 'tests\Test-RemovalBackend.ps1')
$baseline=$script:passed
# Synthetic Windows inventory. No actual driver, queue, port or package is changed.
function Reset-ForcedCase {
 Reset-Case
 $script:FileRows=@([pscustomobject]@{Path=(Join-Path $env:WINDIR 'System32\spool\DRIVERS\x64\3\ISAFOM_TEST_A.DLL');Sha256=('A'*64)},
  [pscustomobject]@{Path=(Join-Path $env:WINDIR 'System32\spool\DRIVERS\x64\3\ISAFOM_TEST_B.DLL');Sha256=('B'*64)})
 $script:Drivers[0]|Add-Member DependentFiles @($script:FileRows[0].Path)
 $script:Drivers[1]|Add-Member DependentFiles @($script:FileRows[0].Path)
 $script:Drivers+=@([pscustomobject]@{Name='Kyocera TASKalfa 5052ci KX';PrinterEnvironment='Windows x64';DriverVersion=[uint64]0x00080002026F0000;DependentFiles=@($script:FileRows[0].Path,$script:FileRows[1].Path)},
  [pscustomobject]@{Name='Kyocera TASKalfa 4052ci KX';PrinterEnvironment='Windows x64';DriverVersion=[uint64]0x00080002026F0000;DependentFiles=@($script:FileRows[1].Path)})
 $script:Queues+=@([pscustomobject]@{Name='Kyocera reparto A';DriverName='Kyocera TASKalfa 5052ci KX';PortName='IP_Reparto_A';Comment='';Shared=$true;Type='Local'},
  [pscustomobject]@{Name='Kyocera reparto B';DriverName='Kyocera TASKalfa 4052ci KX';PortName='IP_Reparto_B';Comment='';Shared=$false;Type='Local'})
 $script:State.Ports+=@([pscustomobject]@{Name='IP_Reparto_A';Host='192.0.2.10';Protocol=1;PortNumber=9100},
  [pscustomobject]@{Name='IP_Reparto_B';Host='192.0.2.11';Protocol=1;PortNumber=9100},
  [pscustomobject]@{Name='IP_140.164.75.11_OLD';Host=$s.FallbackIPv4;Protocol=1;PortNumber=9100})
 $script:PresentPackages=@{'oem42.inf'=$true;'oem43.inf'=$true;'oem44.inf'=$true}
 $script:ForcedPackages=0;$script:BusyQueue=$null;$script:JobRace=$false
 $script:AuxPorts=@()
}
function Get-IsafomExtraPortFacts {param($Names) $script:AuxPorts|Where-Object {$_.Name -cin $Names}}
function Remove-PrinterPort {param($Name,$ErrorAction)
 $script:Writes++;$script:State.Ports=@($script:State.Ports|Where-Object Name -CNE $Name);$script:AuxPorts=@($script:AuxPorts|Where-Object Name -CNE $Name)
}
function Get-IsafomPackageFacts {param($s,$allDrivers)
 $selected=@($allDrivers|Where-Object {$_.Name -cin @($s.DriverName,'Kyocera CS 3554ci KX')})
 [pscustomobject]@{Packages=@(if($script:PresentPackages['oem42.inf']){[pscustomobject]@{Name='oem42.inf';Sha256=('A'*64)}});
 DriverNames=@($selected|ForEach-Object Name);Registrations=@($selected|ForEach-Object {[pscustomobject]@{Name=$_.Name;Environment=$_.PrinterEnvironment}});Unresolved=$false}
}
function Get-IsafomPackagesForDrivers {param($Drivers)
 $names=@($Drivers|ForEach-Object Name)
 $packages=@(if($script:PresentPackages['oem42.inf']){[pscustomobject]@{Name='oem42.inf';Sha256=('A'*64)}}
  if('Kyocera TASKalfa 5052ci KX' -cin $names -and $script:PresentPackages['oem43.inf']){[pscustomobject]@{Name='oem43.inf';Sha256=('B'*64)}}
  if('Kyocera TASKalfa 4052ci KX' -cin $names -and $script:PresentPackages['oem44.inf']){[pscustomobject]@{Name='oem44.inf';Sha256=('C'*64)}})
 [pscustomobject]@{Packages=$packages;Unresolved=$false}
}
function Get-IsafomRemovalFileFacts {param($AllDrivers,$Registrations)
 $selected=@($AllDrivers|Where-Object {$d=$_;@($Registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count -gt 0})
 $paths=@(Get-IsafomDriverPaths $selected)
 $rows=@($script:FileRows|Where-Object {$_.Path -iin $paths})
 $shared=@(foreach($d in $AllDrivers){
  if(@($Registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count){continue}
  $references=@(Get-IsafomDriverPaths @($d))
  if(@($rows|Where-Object {$_.Path -iin $references}).Count){'File condivisi: '+$d.Name}
 })
 [pscustomobject]@{Files=$rows;SharedBy=$shared}
}
function Get-PrintJob {param($PrinterName) if($script:JobReadFails){throw 'UnknownJobs'};if($PrinterName -ceq $script:BusyQueue){[pscustomobject]@{Id=1}}}
function Remove-IsafomOemPackage {param($package,[bool]$ForceRemoval=$false)
 $script:Writes++;if($ForceRemoval){$script:ForcedPackages++};$script:PresentPackages[$package.Name]=$false
 [pscustomobject]@{Removed=$true;RestartRequired=$false}
}
function Test-IsafomOemPackagePresent {param($package) [bool]$script:PresentPackages[$package.Name]}

Reset-ForcedCase
$normal=Get-IsafomRemovalPlan $s $owner
Check (-not $normal.CanProceed -and $normal.BlockedCode -ceq 'RemovalShared') 'Ordinary removal still protects drivers sharing the legacy files'
$forced=Get-IsafomRemovalPlan $s $owner $true
Check ($forced.CanProceed -and $forced.ForceRemoval -and $forced.DriverRegistrations.Count -eq 4 -and $forced.Packages.Count -eq 3) 'Force discovers transitive 5052ci/4052ci registrations and all three packages'
Check ($forced.QueuesToRemove.Count -eq 3 -and $forced.PortsToRemove.Count -eq 4 -and $script:Writes -eq 0) 'Force preview includes all affected queues, their ports and orphaned ISAFOM IP aliases without mutations'
Check ($forced.Fingerprint -cne $normal.Fingerprint) 'Force confirmation has a distinct fingerprint even before any mutation'
Refused 'RemovalChanged' {Invoke-IsafomRemoval $s $owner $normal.Fingerprint $true $false $true}
Check ($script:Writes -eq 0) 'Ordinary confirmation cannot be reused to authorize force'
$result=Invoke-IsafomRemoval $s $owner $forced.Fingerprint $true $false $true
Check ($result.Removal.DriverRemoved -and $result.Removal.PackageRemoved -and $result.Removal.QueueAbsent -and $result.Removal.PortRemoved -and $script:Drivers.Count -eq 0 -and $script:Queues.Count -eq 0 -and $script:State.Ports.Count -eq 0 -and $script:ForcedPackages -eq 3) 'Force removes the complete reviewed closure and every reviewed port'

Reset-ForcedCase
$script:Queues=@()
$forced=Get-IsafomRemovalPlan $s $owner $true
Check ($forced.CanProceed -and $forced.DriverRegistrations.Count -eq 4 -and $forced.PortsToRemove.Count -eq 2) 'Orphaned legacy registrations and target ports remain force-removable with no queue'

Reset-ForcedCase
$script:Drivers+=@([pscustomobject]@{Name='Unrelated HP driver';PrinterEnvironment='Windows x64';DependentFiles=@($script:FileRows[0].Path)})
Check (-not (Get-IsafomRemovalPlan $s $owner $true).CanProceed) 'Force never silently expands into a non-Kyocera driver'

Reset-ForcedCase
$script:Queues+=@([pscustomobject]@{Name='HP sharing port';DriverName='HP';PortName='IP_Reparto_A';Comment='';Shared=$false;Type='Local'})
Check (-not (Get-IsafomRemovalPlan $s $owner $true).CanProceed) 'A reviewed port still used by an unrelated printer blocks destructive cleanup'

Reset-ForcedCase
$script:BusyQueue='Kyocera reparto B'
Check ((Get-IsafomRemovalPlan $s $owner $true).BlockedCode -ceq 'ExistingJobs') 'A pending job on any additionally affected queue blocks force'

Reset-ForcedCase
$forced=Get-IsafomRemovalPlan $s $owner $true;$script:BusyQueue='Kyocera reparto A'
Refused 'RemovalChanged' {Invoke-IsafomRemoval $s $owner $forced.Fingerprint $true $false $true}
Check ($script:Writes -eq 0) 'A job appearing after force review prevents all writes'

Reset-ForcedCase
$script:BoundDevices=@('Unreviewed physical device')
Check (-not (Get-IsafomRemovalPlan $s $owner $true).CanProceed) 'Force does not uninstall an unreviewed physical PnP device'

Reset-ForcedCase
$forced=Get-IsafomRemovalPlan $s $owner $true
$script:State.Ports+=@([pscustomobject]@{Name='New orphan';Host=$s.FallbackIPv4;Protocol=1;PortNumber=9100})
Refused 'RemovalChanged' {Invoke-IsafomRemoval $s $owner $forced.Fingerprint $true $false $true}
Check ($script:Writes -eq 0) 'A new target port invalidates the reviewed force scope'
Reset-ForcedCase
$script:Queues[1].PortName='WSD-TEST-ONLY'
$script:AuxPorts=@([pscustomobject]@{Name='WSD-TEST-ONLY';Host='';Protocol=0;PortNumber=0;Monitor='WSD Port'})
$forced=Get-IsafomRemovalPlan $s $owner $true
Check (@($forced.PortsToRemove|Where-Object Name -CEQ 'WSD-TEST-ONLY').Count -eq 1) 'Force review also includes a WSD port used by an affected Kyocera queue'
$result=Invoke-IsafomRemoval $s $owner $forced.Fingerprint $true $false $true
Check ($result.Removal.PortRemoved -and $script:AuxPorts.Count -eq 0) 'A reviewed unused WSD port is removed and independently checked absent'
Reset-ForcedCase
$first=Get-IsafomRemovalPlan $s $owner $true
[Array]::Reverse($script:Drivers)
$second=Get-IsafomRemovalPlan $s $owner $true
Check ($first.Fingerprint -ceq $second.Fingerprint) 'Driver enumeration order does not invalidate an unchanged force confirmation'
Write-Output ('RESULT '+($script:passed-$baseline)+' forced-removal checks passed; all OS mutations mocked.')
