# No personal names, Account IDs, PINs or passwords are accepted by this backend.
# JSON is supplied through stdin; command text is embedded and constant.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$InformationPreference = 'SilentlyContinue'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)

function Test-IsafomWindows([int]$Build,[int]$ProductType) {
    # Windows 10 Anniversary Update is the oldest .NET 10 Windows client baseline.
    # Server, Windows 7/8 and native ARM/x86 are handled independently/refused.
    $Build -ge 14393 -and $ProductType -eq 1
}

function Test-IsafomCatalog([string]$Path) {
    # A PowerShell 7 parent can supply PSModulePath entries that shadow the
    # desktop Security module. Load the executing engine's own system module.
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
    $signature=Get-AuthenticodeSignature -LiteralPath $Path
    # RC3 is vendor-signed. Pin the exact supplied catalogue and signer, in
    # addition to Windows trust validation; do not accept arbitrary publishers.
    if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate){return $false}
    $hash=Get-IsafomFileHash $Path
    ($signature.SignerCertificate.Thumbprint -ceq 'AA2098F3E70AC791261FF95930AE4C981B28C0D3' -and
        $hash -ceq 'D6B3FDB72E707BABF8C4FE8E73DF42DA99D5C5B7F69A599EB4F1AD4CE04E6E7D') -or
    ($signature.SignerCertificate.Thumbprint -ceq 'BDC9B8C37C6B71791A584EC19F2069D98042DC0A' -and
        $hash -ceq 'A463C131B4190979ADD39D2A08F73B6A76C801E9FD747B59707514C0216BB220')
}

function Get-IsafomSnapshot {
    param($s,[string]$OwnerSid=([Security.Principal.WindowsIdentity]::GetCurrent().User.Value))
    $marker=Get-IsafomOwnerMarker $OwnerSid
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    $processors = @(Get-CimInstance -ClassName Win32_Processor)
    $service = Get-Service -Name Spooler
    $allDrivers = @(Get-PrinterDriver)
    $drivers = @($allDrivers | Where-Object { $_.Name -ceq $s.DriverName } | ForEach-Object {
        $version = 'unknown'
        # DRIVER_INFO package version, not the unrelated version of the UI DLL.
        if ($null -ne $_.PSObject.Properties['DriverVersion'] -and [uint64]$_.DriverVersion -ne 0) {
            $v = [uint64]$_.DriverVersion
            $version = '{0}.{1}.{2}.{3}' -f (($v -shr 48) -band 65535),(($v -shr 32) -band 65535),(($v -shr 16) -band 65535),($v -band 65535)
        }
        [ordered]@{ Name = $_.Name; Version = $version }
    })
    $ports = @(Get-CimInstance -ClassName Win32_TCPIPPrinterPort | ForEach-Object {
        [ordered]@{ Name=$_.Name; Host=$_.HostAddress; Protocol=[int]$_.Protocol; PortNumber=[int]$_.PortNumber;
            SnmpEnabled=[bool]$_.SNMPEnabled; Community=$_.SNMPCommunity; SnmpIndex=[int]$_.SNMPDevIndex }
    })
    $queues = @(Get-Printer | ForEach-Object {
        $jobs = 0
        if ($_.Name -ceq $s.PrinterName -or $_.DriverName -ceq $s.DriverName) {
            try { $jobs = @(Get-PrintJob -PrinterName $_.Name).Count } catch { $jobs = -1 }
        }
        [ordered]@{ Name=$_.Name; DriverName=$_.DriverName; PortName=$_.PortName; Shared=[bool]$_.Shared;
            Jobs=$jobs; Managed=(Test-IsafomManagedQueue $_.Name $_.Comment $marker) }
    })
    [ordered]@{ SupportedWindows=(Test-IsafomWindows ([int]$os.BuildNumber) ([int]$os.ProductType));
        X64=([Environment]::Is64BitProcess -and $processors.Count -gt 0 -and @($processors | Where-Object { $_.Architecture -ne 9 }).Count -eq 0);
        SpoolerRunning=($service.Status -eq 'Running'); Drivers=$drivers; Ports=$ports; Queues=$queues }
}

function Get-IsafomOwnerMarker([string]$sid) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { 'CNR-ISAFOM Printer Installer v2 '+([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sid))).Replace('-','')) }
    finally { $sha.Dispose() }
}

function Test-IsafomQueueIdentity([string]$comment,[string]$storedOwner,[string]$marker) {
    ($comment -ceq $marker) -or ($comment -ceq ($marker+' pending')) -or
        ($comment -ceq 'Autore: Andrea Esposito - andrea.esposito@cnr.it' -and $storedOwner -ceq $marker)
}

function Test-IsafomManagedQueue([string]$name,[string]$comment,[string]$marker) {
    $storedOwner=$null
    if($comment -ceq 'Autore: Andrea Esposito - andrea.esposito@cnr.it') {
        # Read only our namespaced REG_SZ, never the vendor configuration or personal codes.
        $key=$null
        try {
            $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Print\Printers\'+$name+'\PrinterDriverData',$false)
            if($null -ne $key -and $key.GetValueNames() -ccontains 'CNR_ISAFOM_InstallerOwnerV2' -and
                $key.GetValueKind('CNR_ISAFOM_InstallerOwnerV2') -eq [Microsoft.Win32.RegistryValueKind]::String) {
                $storedOwner=$key.GetValue('CNR_ISAFOM_InstallerOwnerV2',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        } catch { return $false } finally { if($null -ne $key){$key.Dispose()} }
    }
    Test-IsafomQueueIdentity $comment $storedOwner $marker
}

function Test-IsafomQueueSecurity([string]$sddl,[string]$sid) {
    $sd=[Security.AccessControl.RawSecurityDescriptor]::new($sddl)
    if($null -eq $sd.DiscretionaryAcl -or $sd.DiscretionaryAcl.Count -eq 0){return $false}
    $userGrant=$false
    foreach($ace in $sd.DiscretionaryAcl) {
        if($ace -isnot [Security.AccessControl.CommonAce] -or $ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed -or $ace.SecurityIdentifier.Value -cnotin @($sid,'S-1-5-18','S-1-5-32-544')){return $false}
        if($ace.SecurityIdentifier.Value -ceq $sid -and ($ace.AccessMask -band 0xF000C) -eq 0xF000C){$userGrant=$true}
    }
    return $userGrant
}

function Test-IsafomTarget { param($s,$hostAddress)
    ($hostAddress -ieq $s.Host) -or ($hostAddress -ceq $s.FallbackIPv4)
}
function Test-IsafomPort { param($s,$p)
    (Test-IsafomTarget $s $p.Host) -and $p.Protocol -eq 1 -and $p.PortNumber -eq $s.PortNumber -and
    $p.SnmpEnabled -and $p.Community -ceq $s.SnmpCommunity -and $p.SnmpIndex -eq $s.SnmpIndex
}

$step='Inspect'
try {
    # The bootstrap passes the already-read request inside this process. Keeping
    # it in memory avoids Console.InputEncoding resetting a replacement reader.
    $requestJson=if($args.Count -eq 1){[string]$args[0]}else{[Console]::In.ReadToEnd()}
    $request = $requestJson | ConvertFrom-Json
    $s = $request.Settings
    $forceRemoval=$null -ne $request.PSObject.Properties['ForceRemoval'] -and [bool]$request.ForceRemoval
    if ($request.Mode -ceq 'Inspect') {
        Get-IsafomSnapshot $s | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if ($request.Mode -ceq 'PlanRemoval') {
        Get-IsafomRemovalPlan $s ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) $forceRemoval | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if ($request.Mode -ceq 'SelectiveInventory') {
        $rows=@(Get-IsafomSelectiveInventory $s ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)|Select-Object * -ExcludeProperty Object,IdentityComment)
        ConvertTo-Json -InputObject $rows -Depth 8 -Compress
        exit 0
    }
    if ($request.Mode -ceq 'PlanSelectiveRemoval') {
        Get-IsafomSelectivePlan $s ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) @($request.QueueIds) | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if ($request.Mode -cnotin @('Ensure','Remove','RemoveSelected','FinalizeRemovalFiles')) { throw 'Protocol' }
    $ownerSid = [Security.Principal.SecurityIdentifier]::new([string]$request.OwnerSid)
    if ($ownerSid.Value -notmatch '^S-1-5-21-(\d+-){3}\d+$') { throw 'Protocol' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'MachineFailure' }
    if($request.Mode -ceq 'FinalizeRemovalFiles'){
        Complete-IsafomRemovalFiles|ConvertTo-Json -Depth 5 -Compress
        exit 0
    }
    if($request.Mode -ceq 'Remove'){
        Invoke-IsafomRemoval $s $ownerSid.Value ([string]$request.PlanFingerprint) ([bool]$request.RemoveDriver) ([bool]$request.AllowSpoolerRestart) $forceRemoval | ConvertTo-Json -Depth 8 -Compress
        exit 0
    }
    if($request.Mode -ceq 'RemoveSelected'){
        Invoke-IsafomSelectiveRemoval $s $ownerSid.Value @($request.QueueIds) ([string]$request.PlanFingerprint)|ConvertTo-Json -Depth 10 -Compress
        exit 0
    }
    if ($request.Endpoint -ine $s.Host -and ($request.Endpoint -cne $s.FallbackIPv4 -or -not $s.AllowFallbackOnDnsFailure)) { throw 'Protocol' }
    $state = Get-IsafomSnapshot $s $ownerSid.Value
    if (-not $state.SupportedWindows -or -not $state.X64 -or -not $state.SpoolerRunning) { throw 'MachineFailure' }
    if (@($state.Drivers | Where-Object { $_.Version -cne $s.DriverVersion }).Count) { throw 'DriverConflict' }
    $targetQueues = @($state.Queues | Where-Object {
        $q = $_
        $q.Name -ceq $s.PrinterName -or ($q.DriverName -ceq $s.DriverName -and
            @($state.Ports | Where-Object { $_.Name -ceq $q.PortName -and (Test-IsafomTarget $s $_.Host) }).Count -gt 0)
    })
    if ($targetQueues.Count -gt 1) { throw 'AmbiguousQueue' }
    $queue = $null
    if ($targetQueues.Count -eq 1) { $queue = $targetQueues[0] }
    if ($null -ne $queue) {
        if (-not $queue.Managed) { throw 'QueueConflict' }
        $existing=Get-Printer -Name $queue.Name -Full
        $pending=$existing.Comment -ceq ((Get-IsafomOwnerMarker $ownerSid.Value)+' pending')
        if(-not $pending -and -not (Test-IsafomQueueSecurity $existing.PermissionSDDL $ownerSid.Value)){throw 'QueueConflict'}
        if ($queue.Shared -or $queue.DriverName -cne $s.DriverName) { throw 'QueueConflict' }
        if ($queue.Jobs -ne 0) { throw 'ExistingJobs' }
        if (-not $queue.Managed -and @($state.Ports | Where-Object { $_.Name -ceq $queue.PortName -and (Test-IsafomTarget $s $_.Host) }).Count -eq 0) { throw 'QueueConflict' }
    }
    $correctPorts = @($state.Ports | Where-Object { $_.Host -ieq $request.Endpoint -and (Test-IsafomPort $s $_) })
    $portName = $s.PortName
    if ($correctPorts.Count -gt 0) { $portName = $correctPorts[0].Name }
    elseif (@($state.Ports | Where-Object { $_.Name -ceq $portName }).Count -gt 0) { throw 'PortConflict' }
    if ($null -ne $queue -and $queue.PortName -cne $portName -and -not $queue.Managed) { throw 'QueueConflict' }

    # No mutations occur above this point. The helper has already authenticated the package.
    if ($state.Drivers.Count -eq 0) {
        $step='Driver'
        $inf = $request.InfPath
        if (-not (Test-Path -LiteralPath $inf -PathType Leaf)) { throw 'DriverIntegrity' }
        $catalog = Join-Path ([IO.Path]::GetDirectoryName($inf)) 'kyomita.cat'
        if (-not (Test-IsafomCatalog $catalog)) { throw 'DriverSignature' }
        $null = & "$env:WINDIR\System32\pnputil.exe" /add-driver $inf
        if ($LASTEXITCODE -eq 3010) { throw 'MachineFailure' }
        if ($LASTEXITCODE -ne 0) { throw 'DriverIntegrity' }
        # Add-PrinterDriver requires an INF already in the Driver Store.
        $stored = @(Get-WindowsDriver -Online -All | Where-Object {
            $_.ClassName -eq 'Printer' -and $_.Version.ToString() -eq $s.DriverVersion -and
            (Split-Path -Leaf $_.OriginalFileName) -ieq 'OEMSETUP.INF' -and $_.ProviderName -match '^Kyocera'
        })
        if ($stored.Count -ne 1) { throw 'DriverConflict' }
        Add-PrinterDriver -Name $s.DriverName -PrinterEnvironment 'Windows x64' -InfPath $stored[0].OriginalFileName | Out-Null
        $state = Get-IsafomSnapshot $s $ownerSid.Value
        if ($state.Drivers.Count -ne 1 -or $state.Drivers[0].Version -cne $s.DriverVersion) { throw 'DriverConflict' }
    }
    if ($correctPorts.Count -eq 0) {
        $step='Port'
        if ($request.Endpoint -ine $s.Host -and ($request.Endpoint -cne $s.FallbackIPv4 -or -not $s.AllowFallbackOnDnsFailure)) { throw 'Protocol' }
        Add-PrinterPort -Name $portName -PrinterHostAddress $request.Endpoint -PortNumber $s.PortNumber -SNMP $s.SnmpIndex -SNMPCommunity $s.SnmpCommunity | Out-Null
    }
    if ($null -eq $queue) {
        $step='Queue'
        $sddl = 'D:P(A;;0xF000C;;;SY)(A;;0xF000C;;;BA)(A;;0xF000C;;;'+$ownerSid.Value+')'
        $marker=Get-IsafomOwnerMarker $ownerSid.Value
        Add-Printer -Name $s.PrinterName -DriverName $s.DriverName -PortName $portName -Shared:$false -Comment ($marker+' pending') -PermissionSDDL $sddl | Out-Null
        $step='Pause'
        $created=Get-CimInstance Win32_Printer|Where-Object Name -CEQ $s.PrinterName
        if($null -eq $created -or (Invoke-CimMethod -InputObject $created -MethodName Pause).ReturnValue -ne 0){throw 'MachineFailure'}
    }
    elseif ($queue.PortName -cne $portName) {
        # Only our managed queue can be repaired. No shared port is changed.
        if (@(Get-PrintJob -PrinterName $queue.Name).Count -ne 0) { throw 'ExistingJobs' }
        Set-Printer -Name $queue.Name -PortName $portName | Out-Null
    }
    # The Add-Printer provider does not reliably apply PermissionSDDL at creation.
    # Apply the DACL separately, before any personal values are accepted.
    $step='Queue'
    $current=Get-Printer -Name $s.PrinterName -Full
    if($current.Comment -ceq ((Get-IsafomOwnerMarker $ownerSid.Value)+' pending')) {
        $sddl='D:P(A;;0xF000C;;;SY)(A;;0xF000C;;;BA)(A;;0xF000C;;;'+$ownerSid.Value+')'
        Set-Printer -Name $current.Name -PermissionSDDL $sddl
        if(-not (Test-IsafomQueueSecurity (Get-Printer -Name $current.Name -Full).PermissionSDDL $ownerSid.Value)){throw 'QueueConflict'}
    }
    # Do not set the Windows default printer. Do not alter global DEVMODE or personal codes here.
    '{"Ok":true,"Code":null,"Step":"Complete"}'
}
catch {
    # Never emit exception text or native command output.
    $allowed = @('Protocol','DriverConflict','AmbiguousQueue','QueueConflict','ExistingJobs','PortConflict','DriverIntegrity','DriverSignature','SpoolerUnavailable','RemovalChanged','RemovalBlocked','RemovalShared','Verification','Configuration')
    $code = 'MachineFailure'
    if ($allowed -ccontains $_.Exception.Message) { $code = $_.Exception.Message }
    $nativeCode=$null
    if($_.FullyQualifiedErrorId -match '0x[0-9a-fA-F]{8}'){$nativeCode=$Matches[0]}
    @{ Ok=$false; Code=$code; Step=$step; NativeCode=$nativeCode } | ConvertTo-Json -Compress
    exit 1
}
