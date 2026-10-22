#requires -Version 5.1
[CmdletBinding()]
param([string]$ProjectRoot='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $ProjectRoot){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
foreach($relative in @('src\Isafom.Windows\Scripts\PrinterBackend.ps1','src\Isafom.Windows\Scripts\RemovalBackend.ps1')){
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $ProjectRoot $relative),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'BackendParseFailed'}
    foreach($node in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
        . ([ScriptBlock]::Create($node.Extent.Text))
    }
}
# All mutation cmdlets, package and storage providers are mocked in this process.
$s=Get-Content -LiteralPath (Join-Path $ProjectRoot 'config\printer-settings.json') -Raw|ConvertFrom-Json
$owner='S-1-5-21-1-2-3-1001';$marker=Get-IsafomOwnerMarker $owner
function Reset-Case {
    $script:Queues=@([pscustomobject]@{Name=$s.PrinterName;DriverName=$s.DriverName;PortName=$s.PortName;Comment=$marker;Shared=$false;Type='Local'})
    $script:Drivers=@([pscustomobject]@{Name=$s.DriverName;PrinterEnvironment='Windows x64';DriverVersion=[uint64]0x0008000509650000},
        [pscustomobject]@{Name='Kyocera CS 3554ci KX';PrinterEnvironment='Windows x64';DriverVersion=[uint64]0x0008000509650000})
    $script:State=[pscustomobject]@{SpoolerRunning=$true;Ports=@([pscustomobject]@{Name=$s.PortName;Host=$s.Host;Protocol=1;PortNumber=9100})}
    $script:JobCount=0;$script:JobReadFails=$false;$script:Default=$false;$script:Writes=0
    $script:RaceAfterRemoval=$false;$script:DriverRemovalFails=$false;$script:StoredOwner=$null
    $script:PackageExists=$true;$script:PackageRemovalFails=$false;$script:PackageUnresolved=$false
    $script:BoundDevices=@();$script:StoragePresent=$false
    $script:FileRows=@();$script:SharedFiles=@();$script:FilesFail=$false
    $script:RestartAllowed=$true;$script:RestartCalls=0
}
function Get-IsafomSnapshot {param($s,$OwnerSid) $script:State}
function Get-Printer {$script:Queues}
function Get-PrinterDriver {$script:Drivers}
function Get-PrintJob {param($PrinterName) if($script:JobReadFails){throw 'TestJobsUnavailable'};for($i=0;$i -lt $script:JobCount;$i++){[pscustomobject]@{Id=$i}}}
function Test-IsafomOriginalDefault {param($OwnerSid,$QueueName) $script:Default}
function Test-IsafomManagedQueue {param($name,$comment,$marker) Test-IsafomQueueIdentity $comment $script:StoredOwner $marker}
function Get-IsafomPackageFacts {param($s,$allDrivers)
    $packages=@();if($script:PackageExists){$packages=@([pscustomobject]@{Name='oem42.inf';Sha256=('A'*64)})}
    [pscustomobject]@{Packages=$packages;DriverNames=@($allDrivers|ForEach-Object {$_.Name}|Sort-Object -Unique);
        Registrations=@($allDrivers|ForEach-Object {[pscustomobject]@{Name=$_.Name;Environment=$_.PrinterEnvironment}});Unresolved=$script:PackageUnresolved}
}
function Get-IsafomRemovalStorage {param($OwnerSid,$QueueNames)
    [pscustomobject]@{CachePresent=$script:StoragePresent;SettingsPresent=$script:StoragePresent;PreferencesPresent=$script:StoragePresent}
}
function Get-IsafomBoundDevices {param($Packages,$QueueName) $script:BoundDevices}
function Get-IsafomRemovalFileFacts {param($AllDrivers,$Registrations) [pscustomobject]@{Files=$script:FileRows;SharedBy=$script:SharedFiles}}
function Get-IsafomKnownDriverFiles {@()}
function Save-IsafomRemovalFiles {param($Files)}
function Remove-IsafomDriverFiles {param($Files) [pscustomobject]@{Complete=(-not $script:FilesFail);Remaining=if($script:FilesFail){1}else{0};RestartRequired=$script:FilesFail}}
function Restart-IsafomEmptySpooler {$script:RestartCalls++;if($script:RestartAllowed){$script:FilesFail=$false;return $true};return $false}
function Release-IsafomIsolationHosts {param($Files) return -1}
function Remove-Printer {param($Name,$ErrorAction)
    $script:Writes++;$script:Queues=@($script:Queues|Where-Object Name -CNE $Name)
    if($script:RaceAfterRemoval){$script:Queues+=@([pscustomobject]@{Name='Nuova coda concorrente';DriverName=$s.DriverName;PortName='Other';Comment='';Shared=$false;Type='Local'})}
}
function Remove-IsafomDriverRegistration {param($Name,$PrinterEnvironment)
    if($script:DriverRemovalFails){throw 'TestDriverBusy'}
    $script:Writes++;$script:Drivers=@($script:Drivers|Where-Object {$_.Name -cne $Name -or $_.PrinterEnvironment -cne $PrinterEnvironment})
}
function Remove-PrinterPort {param($Name,$ErrorAction)
    $script:Writes++;$script:State.Ports=@($script:State.Ports|Where-Object Name -CNE $Name)
}
function Remove-IsafomOemPackage {param($package)
    if($script:PackageRemovalFails){return [pscustomobject]@{Removed=$false;RestartRequired=$false}}
    $script:Writes++;$script:PackageExists=$false;[pscustomobject]@{Removed=$true;RestartRequired=$false}
}
function Test-IsafomOemPackagePresent {param($package) $script:PackageExists}
$script:passed=0
function Check([bool]$condition,[string]$label){if(-not $condition){throw ('FAIL '+$label)};$script:passed++;Write-Output ('PASS '+$label)}
function Refused([string]$Code,[ScriptBlock]$Action){try{& $Action|Out-Null}catch{if($_.Exception.Message -ceq $Code){return};throw};throw 'ExpectedRefusal'}

Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner
Check ($plan.CanProceed -and $plan.FullRemoval -and $plan.Packages.Count -eq 1 -and $plan.PackageDriverNames.Count -eq 2 -and $script:Writes -eq 0) 'Read-only preview includes both model registrations, OEM package and port'
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true
Check ($result.Removal.QueueAbsent -and $result.Removal.DriverRemoved -and $result.Removal.PackageRemoved -and $result.Removal.PortRemoved -and $script:Writes -eq 5) 'Complete reset removes queue, both models, Driver Store package and port'
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'A second preview verifies that printer assets are already absent'

Reset-Case
$script:Queues+=@([pscustomobject]@{Name='Altra stampante CS';DriverName='Kyocera CS 3554ci KX';PortName='Other';Comment='';Shared=$false;Type='Local'})
$plan=Get-IsafomRemovalPlan $s $owner
Check (-not $plan.CanProceed -and $plan.BlockedCode -ceq 'RemovalShared') 'Another model using the same package blocks the entire reset'
Refused 'RemovalBlocked' {Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true}
Check ($script:Writes -eq 0) 'Shared package protection applies before queue deletion'

Reset-Case
$script:Queues+=@([pscustomobject]@{Name='Altra stampante HP';DriverName='HP';PortName=$s.PortName;Comment='';Shared=$false;Type='Local'})
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Shared TCP port blocks complete reset'

Reset-Case
$script:BoundDevices=@('Altro dispositivo USB')
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Another PnP device using the package blocks reset'

Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:JobCount=1
Refused 'RemovalChanged' {Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true}
Check ($script:Writes -eq 0) 'A job appearing after confirmation prevents all removals'
Check ((Get-IsafomRemovalPlan $s $owner).BlockedCode -ceq 'ExistingJobs') 'Jobs block the preview'

Reset-Case
$script:JobReadFails=$true
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Unknown job count blocks removal'

Reset-Case
$script:Queues[0].Comment=Get-IsafomOwnerMarker 'S-1-5-21-1-2-3-2002'
Check ((Get-IsafomRemovalPlan $s $owner).BlockedCode -ceq 'QueueConflict') 'Another Windows user installer marker is protected'

Reset-Case
$script:Queues[0].Comment='Autore: Andrea Esposito - andrea.esposito@cnr.it';$script:StoredOwner=$marker
Check ((Get-IsafomRemovalPlan $s $owner).CanProceed) 'Author comment and internal owner accepted'
$script:StoredOwner=$null
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Author text alone cannot authorize removal'

Reset-Case
$script:Queues[0].Comment='Installazione manuale'
$plan=Get-IsafomRemovalPlan $s $owner
Check ($plan.ManualQueue -and $plan.CanProceed) 'Exact manual local target is identified explicitly'
$script:State.Ports[0].Host='other.example'
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Different endpoint is protected'

Reset-Case
$script:Queues[0].Shared=$true
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Shared queue is protected'

Reset-Case
$script:Drivers[0].DriverVersion=[uint64]0x0009000000010000
Check ((Get-IsafomRemovalPlan $s $owner).DriverVersion -ceq '9.0.1.0') 'Conflicting version remains removable when unused'

Reset-Case
$script:Default=$true
Check ((Get-IsafomRemovalPlan $s $owner).IsDefault) 'Original user default-printer consequence is shown'

Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:RaceAfterRemoval=$true
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true
Check ($result.Removal.QueueAbsent -and -not $result.Removal.DriverRemoved -and -not $result.Removal.PackageRemoved) 'Concurrent new dependency produces partial status and preserves the driver'

Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:PackageRemovalFails=$true
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true
Check (-not $result.Removal.PackageRemoved -and $script:PackageExists) 'OS refusal leaves the package present and never reports complete reset'

Reset-Case
$script:PackageUnresolved=$true
Check (-not (Get-IsafomRemovalPlan $s $owner).CanProceed) 'Unidentified driver package blocks destructive operations'

Reset-Case
$script:Queues=@();$script:Drivers=@();$script:PackageExists=$false;$script:State.Ports=@();$script:StoragePresent=$true
Check ((Get-IsafomRemovalPlan $s $owner).CanProceed) 'Residual cache, administrative settings and profile preferences are still resettable'

Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner
Refused 'RemovalBlocked' {Invoke-IsafomRemoval $s $owner $plan.Fingerprint $false}
Check ($script:Writes -eq 0) 'Partial selection is no longer allowed by the complete-reset action'
Refused 'Protocol' {Invoke-IsafomRemoval $s $owner 'not-a-fingerprint' $true}
Check ($script:Writes -eq 0) 'Malformed confirmation token cannot mutate'
Reset-Case
$script:Drivers+=@([pscustomobject]@{Name=$s.DriverName;PrinterEnvironment='Windows NT x86';DriverVersion=[uint64]0x0008000509650000})
$plan=Get-IsafomRemovalPlan $s $owner
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true
Check ($plan.DriverRegistrations.Count -eq 3 -and $result.Removal.DriverRemoved -and $script:Drivers.Count -eq 0) 'All approved architectures are removed from their own environment'
Reset-Case
$script:SharedFiles=@('File condivisi: altra stampante')
Check ((Get-IsafomRemovalPlan $s $owner).BlockedCode -ceq 'RemovalShared') 'A driver file shared with another registration blocks all mutations'
Reset-Case
$script:FileRows=@([pscustomobject]@{Path='C:\Windows\System32\spool\DRIVERS\x64\3\KMFS85O5.DLL';Sha256=('A'*64)})
$plan=Get-IsafomRemovalPlan $s $owner;$script:FilesFail=$true
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true
Check (-not $result.Removal.DriverFilesRemoved -and $result.Removal.DriverFilesRemaining -eq 1 -and $result.Removal.RestartRequired) 'Locked file prevents complete status and reports the required restart'
Reset-Case
$script:Queues=@();$script:Drivers=@();$script:PackageExists=$false;$script:State.Ports=@()
$script:FileRows=@([pscustomobject]@{Path='C:\Windows\System32\spool\DRIVERS\x64\3\KMFS85O5.DLL';Sha256=('A'*64)})
Check ((Get-IsafomRemovalPlan $s $owner).CanProceed) 'Persisted file list permits cleanup after registrations and packages are already absent'
Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:FilesFail=$true
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true $true
Check ($result.Removal.DriverFilesRemoved -and $result.Removal.SpoolerRestarted -and -not $result.Removal.RestartRequired -and $script:RestartCalls -eq 1) 'Explicit consent permits one safe restart and a fresh file-removal verification'
Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:FilesFail=$true
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true $false
Check (-not $result.Removal.DriverFilesRemoved -and $script:RestartCalls -eq 0) 'No spooler restart without original user consent'
Reset-Case
$plan=Get-IsafomRemovalPlan $s $owner;$script:FilesFail=$true;$script:RestartAllowed=$false
$result=Invoke-IsafomRemoval $s $owner $plan.Fingerprint $true $true
Check (-not $result.Removal.DriverFilesRemoved -and -not $result.Removal.SpoolerRestarted -and $result.Removal.RestartRequired) 'Busy or unreadable unrelated queues prevent restart and complete status'
Write-Output ('RESULT '+$script:passed+' simulated complete-removal checks passed; no real printers, packages or files modified.')
