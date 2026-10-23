#requires -Version 5.1
param([string]$ProjectRoot='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if(-not $ProjectRoot){$ProjectRoot=Split-Path -Parent $PSScriptRoot}
foreach($file in @('PrinterBackend.ps1','RemovalBackend.ps1','SelectiveRemoval.ps1')) {
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $ProjectRoot ('src\Isafom.Windows\Scripts\'+$file)),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw 'ParseFailure'}
    foreach($node in $ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) { . ([ScriptBlock]::Create($node.Extent.Text)) }
}
$s=Get-Content (Join-Path $ProjectRoot 'config\printer-settings.json') -Raw|ConvertFrom-Json
$owner='S-1-5-21-1-2-3-1001';$marker=Get-IsafomOwnerMarker $owner
$script:passed=0
function Check([bool]$value,[string]$label){if(-not $value){throw ('FAIL '+$label)};$script:passed++;Write-Output ('PASS '+$label)}
function Refused([scriptblock]$action){try{& $action|Out-Null}catch{return};throw 'Expected refusal'}
function Reset-SelectiveCase {
    $script:Queues=@(1..5|ForEach-Object {[pscustomobject]@{QueueId=([guid]('00000000-0000-0000-0000-'+$_.ToString('000000000000'))).ToString('D');
        Name=('Printer '+$_);DriverName=$s.DriverName;PortName=('IP_'+$_);Comment=$marker;Type='Local';Shared=$false;PrinterStatus='Normal'}})
    $script:Queues[2].PortName='IP_1'
    $script:Queues[3].DriverName='HP';$script:Queues[3].Comment=''
    $script:Queues[4].DriverName='Microsoft Print to PDF';$script:Queues[4].Comment=''
    $script:Ports=@(1..5|ForEach-Object {[pscustomobject]@{Name=('IP_'+$_);HostAddress=$s.FallbackIPv4;Protocol=1;PortNumber=9100}})
    $script:Drivers=@([pscustomobject]@{Name=$s.DriverName;PrinterEnvironment='Windows x64';InfPath='';DriverVersion=1})
    $script:Writes=@();$script:Busy='';$script:JobFailure=$false;$script:FailQueue='';$script:NoopQueue='';$script:FailPort='';$script:FailDriver=$false
    $script:DefaultName='Printer 5';$script:NativeCalls=0;$script:AfterDelete=$null;$script:InventoryFailure=$false;$script:PackageExists=$false;$script:PackageRestart=$false
    $script:MissingIdentity='';$script:DuplicateRegistry=$false;$script:MissingProvider='';$script:BoundDevices=@();$script:DriverReadFails=$false
}
function Get-Service {param($Name,$ErrorAction) [pscustomobject]@{Status='Running'}}
function Get-Printer {param($ErrorAction) if($script:InventoryFailure){throw 'InventoryFailure'};$script:Queues|Where-Object Name -CNE $script:MissingProvider}
function Get-IsafomQueueRegistry {
    foreach($q in $script:Queues) {
        [pscustomobject]@{Name=$q.Name;QueueId=if($q.Name -ceq $script:MissingIdentity){''}else{$q.QueueId};DriverName=$q.DriverName;PortName=$q.PortName;Comment=$q.Comment;Attributes=64}
    }
    if($script:DuplicateRegistry){$q=$script:Queues[0];[pscustomobject]@{Name='Damaged duplicate';QueueId=$q.QueueId;DriverName=$q.DriverName;PortName=$q.PortName;Comment=$q.Comment;Attributes=64}}
}
function Get-CimInstance {param($ClassName,$ErrorAction) if($ClassName -cne 'Win32_TCPIPPrinterPort'){throw 'UnexpectedQuery'};$script:Ports}
function Get-PrinterDriver {param($ErrorAction) if($script:DriverReadFails){throw 'DriverProviderFailure'};$script:Drivers}
function Test-IsafomOriginalDefault {param($OwnerSid,$QueueName) $QueueName -ceq $script:DefaultName}
function Test-IsafomManagedQueue {param($name,$comment,$marker) Test-IsafomQueueIdentity $comment $null $marker}
function Get-PrintJob {param($PrinterName,$ErrorAction) if($script:JobFailure){throw 'AccessDenied'};if($PrinterName -ceq $script:Busy){[pscustomobject]@{Id=1}}}
function Remove-Printer {param($InputObject,$ErrorAction)
    $script:Writes+=@('Queue:'+ $InputObject.QueueId)
    if($InputObject.Name -ceq $script:FailQueue){throw 'AccessDenied'}
    if($InputObject.Name -ceq $script:NoopQueue){return}
    $script:Queues=@($script:Queues|Where-Object QueueId -CNE $InputObject.QueueId)
    if($null -ne $script:AfterDelete){& $script:AfterDelete}
}
function Remove-IsafomSelectiveQueueNative {param($Row)
    $script:NativeCalls++
    if($Row.Name -ceq $script:FailQueue -or $Row.Name -ceq $script:NoopQueue){throw 'AccessDenied'}
    $script:Writes+=@('Native:'+ $Row.QueueId);$script:Queues=@($script:Queues|Where-Object QueueId -CNE $Row.QueueId)
}
function Remove-IsafomSelectiveQueue {param($Row)
    if($null -eq $Row.Object){throw 'ProviderRegistrationMissing'}
    Remove-Printer -InputObject $Row.Object -ErrorAction Stop
}
function Remove-PrinterPort {param($Name,$ErrorAction)
    $script:Writes+=@('Port:'+ $Name);if($Name -ceq $script:FailPort){throw 'Busy'}
    $script:Ports=@($script:Ports|Where-Object Name -CNE $Name)
}
function Remove-IsafomDriverRegistration {param($Name,$PrinterEnvironment)
    $script:Writes+=@('Driver:'+ $Name);if($script:FailDriver){return}
    $script:Drivers=@($script:Drivers|Where-Object Name -CNE $Name)
}
function Get-IsafomPackagesForDrivers {param($Drivers) [pscustomobject]@{Packages=@(if($script:PackageExists){[pscustomobject]@{Name='oem42.inf';Sha256='B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7'}});Unresolved=$false}}
function Get-IsafomBoundDevices {param($Packages,$QueueName) $script:BoundDevices}
function Get-IsafomSelectiveBoundDevices {param($Packages,$SelectedIds) $script:BoundDevices}
function Get-IsafomKnownDriverFiles {@()}
function Remove-IsafomOemPackage {param($package,[bool]$ForceRemoval)
    if($ForceRemoval){throw 'ForceNeverAllowed'};$script:Writes+=@('Package:'+ $package.Name);$script:PackageExists=$false
    [pscustomobject]@{Removed=$true;RestartRequired=$script:PackageRestart}
}
function Test-IsafomOemPackagePresent {param($Package) $script:PackageExists}
function Restart-Service {throw 'GlobalRestartForbidden'}
function Stop-Service {throw 'GlobalStopForbidden'}
function Remove-Item {throw 'UnboundedDeleteForbidden'}
function Approve([int[]]$Indexes=@(0,1)){Get-IsafomSelectivePlan $s $owner @($Indexes|ForEach-Object {$script:Queues[$_].QueueId})}
function Execute($plan){Invoke-IsafomSelectiveRemoval $s $owner @($plan.Queues|ForEach-Object QueueId) $plan.Fingerprint}

Reset-SelectiveCase
$before=ConvertTo-Json -InputObject @($script:Queues|Select-Object -Skip 2) -Compress
$plan=Approve
Check ($plan.Queues.Count -eq 2 -and $plan.Ports.Count -eq 1 -and $plan.Drivers.Count -eq 0 -and $script:Writes.Count -eq 0) 'Preview selects exactly two queues and excludes their shared driver and port'
$result=Execute $plan
Check ($script:Queues.Count -eq 3 -and (ConvertTo-Json -InputObject $script:Queues -Compress) -ceq $before) 'ACCEPTANCE: two of five removed; all other queue metadata remains identical'
Check ($script:Ports.Name -contains 'IP_1' -and $script:Ports.Name -notcontains 'IP_2' -and $script:Drivers.Count -eq 1) 'Shared driver and shared port retained; only dedicated selected port deleted'
Check (@($result.SelectiveRemoval|Where-Object Outcome -CNE 'Rimossa completamente').Count -eq 0) 'Individual successful outcomes distinguish intentionally retained shared resources'
$baselineWriteCount=$script:Writes.Count;$null=Execute $plan
Check ($script:Writes.Count -eq $baselineWriteCount) 'Completed request is idempotent and performs no further mutation'

Reset-SelectiveCase
$script:Queues[0].PrinterStatus='Offline';$plan=Approve @(0);$null=Execute $plan
Check ($script:Queues.Count -eq 4) 'Offline queues do not require network reachability'

Reset-SelectiveCase
$script:MissingProvider='Printer 1';$plan=Approve @(0);$result=Execute $plan
Check ($script:NativeCalls -eq 1 -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente') 'Incomplete provider registration is recovered through the exact Windows instance'

Reset-SelectiveCase
$script:NoopQueue='Printer 1';$result=Execute (Approve)
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa' -and $result.SelectiveRemoval[1].Outcome -ceq 'Rimossa completamente') 'Successful command with queue still present is failure; next selected queue continues'

Reset-SelectiveCase
$script:FailQueue='Printer 1';$result=Execute (Approve)
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa' -and $script:Queues.Count -eq 4 -and $script:NativeCalls -eq 1) 'Permission failure receives a bounded targeted retry and does not expand scope'

Reset-SelectiveCase
$script:FailPort='IP_2';$result=Execute (Approve @(1))
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente' -and $result.SelectiveRemoval[0].Residuals.Count -gt 0) 'Intermediate port failure is reported as partial with residuals'

Reset-SelectiveCase
$script:Busy='Printer 1';$result=Execute (Approve @(0))
Check ($script:Writes.Count -eq 0 -and $result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa') 'Pending jobs block only their selected queue without cancelling jobs'

Reset-SelectiveCase
$script:JobFailure=$true;$result=Execute (Approve @(0))
Check ($script:Writes.Count -eq 0 -and $result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa') 'Unreadable jobs and insufficient permissions fail closed'

Reset-SelectiveCase
$plan=Approve @(0);$script:Queues[0].QueueId='11111111-1111-1111-1111-111111111111';$null=Execute $plan
Check ($script:Writes.Count -eq 0 -and $script:Queues.Count -eq 5) 'Same display name with a replacement GUID is never removed'

Reset-SelectiveCase
$plan=Approve @(0);$script:Queues[0].PortName='Reassigned'
Refused {Execute $plan}
Check ($script:Writes.Count -eq 0) 'Configuration changed after review invalidates confirmation'

Reset-SelectiveCase
$script:Queues[1].Name=$script:Queues[0].Name
Refused {Approve @(0)}
Check ($script:Writes.Count -eq 0) 'Duplicate display names cannot authorize ambiguous queue deletion'

Reset-SelectiveCase
$script:DuplicateRegistry=$true
Refused {Approve @(0)}
Check ($script:Writes.Count -eq 0) 'Duplicate Windows GUIDs are protected'

Reset-SelectiveCase
$script:MissingIdentity='Printer 1'
Check (-not (@(Get-IsafomSelectiveInventory $s $owner|Where-Object Name -CEQ 'Printer 1')[0].CanSelect)) 'Missing instance identity is protected instead of falling back to a name'

Reset-SelectiveCase
$script:DefaultName='Printer 1'
$rows=@(Get-IsafomSelectiveInventory $s $owner)
Check ($rows[0].CanSelect -and $rows[0].IsDefault -and -not $rows[3].CanSelect -and -not $rows[4].CanSelect) 'Associated default queue is selectable; unrelated and system queues remain protected'
$before=ConvertTo-Json -InputObject @($script:Queues|Select-Object -Skip 2) -Compress
$plan=Approve
Check (@($plan.Queues|Where-Object IsDefault).Count -eq 1) 'Reviewed fingerprint explicitly includes selected default printer status'
$null=Assert-IsafomSelectiveDefault $rows[0] $owner
$result=Execute $plan
Check ($script:Queues.Count -eq 3 -and (ConvertTo-Json -InputObject $script:Queues -Compress) -ceq $before -and $script:Ports.Name -contains 'IP_1' -and $script:Drivers.Count -eq 1 -and @($result.SelectiveRemoval|Where-Object Outcome -CNE 'Rimossa completamente').Count -eq 0) 'Selected default and second queue are removed; all three excluded queues and shared resources remain intact'

Reset-SelectiveCase
$script:DefaultName='Printer 1'
$script:AfterDelete={if(@($script:Queues|Where-Object Name -CEQ $script:DefaultName).Count -eq 0){$script:DefaultName=$script:Queues[0].Name}}
$result=Execute (Approve)
Check ($script:Writes[0] -ceq 'Queue:00000000-0000-0000-0000-000000000002' -and $script:Queues.Count -eq 3 -and @($result.SelectiveRemoval|Where-Object Outcome -CNE 'Rimossa completamente').Count -eq 0) 'Selected default is deleted last so Windows automatic reassignment cannot block another selected queue'

Reset-SelectiveCase
$plan=Approve @(0);$script:DefaultName='Printer 1'
Refused {Execute $plan}
Check ($script:Writes.Count -eq 0) 'Becoming default after review invalidates a plan without default acknowledgement'

Reset-SelectiveCase
$script:DefaultName='Printer 1';$plan=Approve @(0);$script:DefaultName='Printer 5'
Refused {Execute $plan}
Check ($script:Writes.Count -eq 0) 'Changing away from the reviewed default status also requires a fresh review'

Reset-SelectiveCase
$row=@(Get-IsafomSelectiveInventory $s $owner|Where-Object Name -CEQ 'Printer 1')[0]
$script:DefaultName='Printer 1'
Refused {Assert-IsafomSelectiveDefault $row $owner}
Check ($script:Writes.Count -eq 0) 'Native handle guard rejects a default change after the final inventory check'

Reset-SelectiveCase
$script:DefaultName='Printer 4';Refused {Approve @(3)}
$script:DefaultName='Printer 5';Refused {Approve @(4)}
Check ($script:Writes.Count -eq 0) 'Default status never makes unrelated or system queues eligible for removal'

Reset-SelectiveCase
$script:Queues[0].Comment='';$script:Queues[0].PortName='Wrong_port';$script:DefaultName='Printer 1'
$script:Queues[1].Comment='';$script:Queues[1].PortName='Missing_port';$script:Queues[1].DriverName='Kyocera ECOSYS M5526cdw KX'
$rows=@(Get-IsafomSelectiveInventory $s $owner)
Check ($rows[0].CanSelect -and $rows[1].CanSelect -and -not $rows[0].Associated -and -not $rows[1].Associated) 'Multiple local Kyocera models with wrong or missing ports are selectable without claiming ISAFOM ownership'
$before=ConvertTo-Json -InputObject @($script:Queues|Select-Object -Skip 2) -Compress
$plan=Approve;$result=Execute $plan
Check ($script:Queues.Count -eq 3 -and (ConvertTo-Json -InputObject $script:Queues -Compress) -ceq $before -and @($result.SelectiveRemoval|Where-Object Outcome -CEQ 'Non rimossa').Count -eq 0) 'Batch removal of two misconfigured Kyocera queues includes the selected default and leaves three excluded queues identical'
Check (@($script:Writes|Where-Object {$_ -notlike 'Queue:*'}).Count -eq 0 -and $script:Drivers.Count -eq 1 -and $script:Ports.Count -eq 5) 'Unknown ports and other-model driver resources are retained during misconfigured Kyocera batch removal'

Reset-SelectiveCase
$script:Queues[0].Comment='';$script:Queues[0].PortName='Missing_port';$script:MissingProvider='Printer 1'
$result=Execute (Approve @(0))
Check ($script:NativeCalls -eq 1 -and $script:Queues.Count -eq 4 -and $result.SelectiveRemoval[0].Outcome -cne 'Non rimossa') 'A manual Kyocera with damaged provider registration and missing port receives targeted recovery'

Reset-SelectiveCase
$script:Queues[0].Comment='';$script:Queues[0].DriverName='';$script:Queues[0].PortName=''
Refused {Approve @(0)}
Check ($script:Writes.Count -eq 0) 'Unidentifiable corrupt queues without installer ownership or a Kyocera driver remain protected'

Reset-SelectiveCase
$script:Queues[0].Shared=$true;Refused {Approve @(0)}
$script:Queues[0].Shared=$false;$script:Queues[0].Type='Connection';Refused {Approve @(0)}
$script:Queues[0].Type='Local';$script:Queues[0].Comment=Get-IsafomOwnerMarker 'S-1-5-21-1-2-3-2002';Refused {Approve @(0)}
$script:Queues[0].Comment='Autore: Andrea Esposito - andrea.esposito@cnr.it';Refused {Approve @(0)}
Check ($script:Writes.Count -eq 0) 'Shared, remote and other-user installations cannot bypass UI protection'

Reset-SelectiveCase
$script:Queues[0].Comment=$marker+' pending';$script:Queues[0].DriverName=''
$plan=Approve @(0);$result=Execute $plan
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente') 'Installer-owned partial installation is removable with its driver missing'

Reset-SelectiveCase
$script:AfterDelete={$script:Queues[2].PortName='IP_2'};$result=Execute (Approve @(1))
Check ($script:Ports.Name -contains 'IP_2') 'New port dependency appearing during removal preserves the resource'

Reset-SelectiveCase
$script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$plan=Approve @(0);$result=Execute $plan
Check ($script:Drivers.Count -eq 0 -and -not $script:PackageExists -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente') 'Unused exact driver and recognized package are removed without force flags'

Reset-SelectiveCase
$script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$script:FailDriver=$true;$result=Execute (Approve @(0))
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente') 'Driver cmdlet success is not enough: registration absence must be verified'

Reset-SelectiveCase
$plan=Approve @(0);$script:AfterDelete={$script:InventoryFailure=$true};$result=Execute $plan
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa' -and $script:Writes.Count -eq 1) 'Inventory failure after deletion prevents cleanup and false success'

Reset-SelectiveCase
Refused {Get-IsafomRemovalPlan $s $owner $true}
Refused {Get-IsafomSelectivePlan $s $owner @()}
Refused {Get-IsafomSelectivePlan $s $owner @($script:Queues[0].QueueId,$script:Queues[0].QueueId)}
Check ($script:Writes.Count -eq 0) 'Legacy force expansion, empty selection and duplicate selection are rejected'

Reset-SelectiveCase
$script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$script:PackageRestart=$true;$result=Execute (Approve @(0))
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente' -and ($result.SelectiveRemoval[0].Residuals -join ' ') -match 'riavvio') 'A required Windows restart is reported as partial even if the INF disappeared'

Reset-SelectiveCase
$script:Queues[2].PortName='Another, ip_2';$script:Queues[2].DriverName=$s.DriverName.ToUpperInvariant()
$plan=Approve @(1);$result=Execute $plan
Check ($plan.Ports.Count -eq 0 -and $plan.Drivers.Count -eq 0 -and $script:Ports.Name -contains 'IP_2') 'Case-insensitive references and pooled ports protect shared resources'

Reset-SelectiveCase
$script:AfterDelete={$script:DefaultName='Printer 2'};$result=Execute (Approve)
Check ($result.SelectiveRemoval[1].Outcome -ceq 'Non rimossa' -and @($script:Queues|Where-Object Name -CEQ 'Printer 2').Count -eq 1) 'A selected queue becoming default during removal is protected'

Reset-SelectiveCase
$script:AfterDelete={$script:Busy='Printer 2'};$result=Execute (Approve)
Check ($result.SelectiveRemoval[1].Outcome -ceq 'Non rimossa' -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente') 'A new job between queue operations blocks only the affected queue'

Reset-SelectiveCase
$script:FailPort='IP_2';$result=Execute (Approve)
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente' -and $result.SelectiveRemoval[1].Outcome -ceq 'Rimossa parzialmente') 'A resource failure is attributed only to its own selected queue'

Reset-SelectiveCase
$script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$script:BoundDevices=@('USB\EXTERNAL');$plan=Approve @(0);$result=Execute $plan
Check ($plan.Drivers.Count -eq 0 -and $plan.Packages.Count -eq 0 -and $script:PackageExists -and $script:Drivers.Count -eq 1) 'External PnP dependency retains the driver and package while removing only the selected queue'

Reset-SelectiveCase
$script:Queues=@($script:Queues[0]);$script:DriverReadFails=$true;$plan=Approve @(0);$result=Execute $plan
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente' -and $script:Writes.Count -eq 1) 'Corrupt resource inventory permits exact queue removal while preserving all unverified resources'

Reset-SelectiveCase
$script:Queues[2].DriverName='';$script:Queues[2].PortName=''
$plan=Approve @(1);$result=Execute $plan
Check ($plan.Ports.Count -eq 0 -and $plan.Drivers.Count -eq 0 -and $script:Writes.Count -eq 1 -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente') 'Unknown dependencies on an excluded damaged queue preserve all resources'

Reset-SelectiveCase
$script:Queues[1].DriverName='Unrecognized driver';$result=Execute (Approve)
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Rimossa completamente' -and $result.SelectiveRemoval[1].Outcome -ceq 'Rimossa parzialmente') 'Unrecognized driver residues are attributed only to the queue that used that driver'

Reset-SelectiveCase
$staleProvider=$script:Queues[0].PSObject.Copy();$staleProvider.QueueId=''
$plan=Approve @(0);$script:AfterDelete={$script:Queues+=@($staleProvider)};$result=Execute $plan
Check ($result.SelectiveRemoval[0].Outcome -ceq 'Non rimossa' -and $script:Writes.Count -eq 1) 'A provider queue persisting without registry identity never counts as successful removal'

# Local byte fixtures only; file/driver mutators remain mocked. No spool path is written.
$fixture=[IO.Path]::GetTempFileName()
$originalHash=${function:Get-IsafomFileHash};$originalPaths=${function:Get-IsafomDriverPaths}
try {
    function Get-IsafomFileHash {param($Path) if($Path -ceq $fixture){return 'B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7'};& $originalHash $Path}
    function Get-IsafomDriverPaths {param($Drivers,[bool]$Strict=$false) foreach($d in $Drivers){if($null -ne $d.PSObject.Properties['FilePaths']){$d.FilePaths}}}
    Reset-SelectiveCase
    $script:Queues=@($script:Queues[0]);$script:PackageExists=$true
    $script:Drivers[0]|Add-Member FilePaths @($fixture)
    $script:Drivers+=@([pscustomobject]@{Name='Kyocera other model';PrinterEnvironment='Windows x64';InfPath=$fixture;FilePaths=@($fixture)})
    $plan=Approve @(0);$result=Execute $plan
    Check ($plan.Drivers.Count -eq 1 -and $plan.Packages.Count -eq 0 -and $plan.Files.Count -eq 0 -and $script:Drivers.Count -eq 1 -and $script:PackageExists -and [IO.File]::Exists($fixture)) 'Shared OEM package and spool file preserve the other model without transitive removal'

    Reset-SelectiveCase
    $script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$script:Drivers[0]|Add-Member FilePaths @($fixture)
    function Get-IsafomKnownDriverFiles {[pscustomobject]@{Name=[IO.Path]::GetFileName($fixture);Hashes=@('B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7')}}
    function Remove-IsafomDriverFiles {param($Files) [pscustomobject]@{Complete=$false;Remaining=1;RestartRequired=$true}}
    $plan=Approve @(0);$result=Execute $plan
    Check ($plan.Files.Count -eq 1 -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente' -and [IO.File]::Exists($fixture)) 'Locked dedicated file is reported as a residue without restarting the spooler or closing hosts'

    Reset-SelectiveCase
    $script:Queues=@($script:Queues[0]);$script:PackageExists=$true;$script:Drivers[0]|Add-Member FilePaths @($fixture)
    function Get-IsafomKnownDriverFiles {@()}
    $plan=Approve @(0);$result=Execute $plan
    Check ($plan.Files.Count -eq 0 -and $plan.Unresolved.Count -eq 1 -and $result.SelectiveRemoval[0].Outcome -ceq 'Rimossa parzialmente') 'Unknown file hashes are retained and explicitly reported instead of being deleted'
} finally {
    [IO.File]::Delete($fixture)
    ${function:Get-IsafomFileHash}=$originalHash;${function:Get-IsafomDriverPaths}=$originalPaths
}

$nativeText=[IO.File]::ReadAllText((Join-Path $ProjectRoot 'src\Isafom.Windows\Scripts\SelectiveRemoval.ps1'))
$nativeMatch=[regex]::Match($nativeText,"(?s)Add-Type -TypeDefinition @'\r?\n(.*?)\r?\n'@")
if(-not ('IsafomSelectiveNative' -as [type])){Add-Type -TypeDefinition $nativeMatch.Groups[1].Value -ErrorAction Stop}
Check ($nativeMatch.Success -and $null -ne ('IsafomSelectiveNative' -as [type])) 'Native queue recovery interop compiles without invoking destructive APIs'
Write-Output ('RESULT '+$script:passed+' selective forced-removal checks passed; all OS mutations mocked.')
