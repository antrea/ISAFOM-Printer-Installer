# Definitions only. Destructive entry point requires the authenticated elevated helper.
function Get-IsafomFileHash([string]$Path){
    $stream=[IO.File]::OpenRead($Path);$sha=[Security.Cryptography.SHA256]::Create()
    try{[BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}
    finally{$sha.Dispose();$stream.Dispose()}
}

function Test-IsafomDriverFilePath([string]$Path){
    if(-not [IO.Path]::IsPathRooted($Path)){throw 'RemovalBlocked'}
    $full=[IO.Path]::GetFullPath($Path)
    $parent=[IO.Path]::GetDirectoryName($full)
    $allowed=@('x64','W32X86','ARM64')|ForEach-Object {Join-Path $env:WINDIR ('System32\spool\DRIVERS\'+$_+'\3')}
    if($parent -notin $allowed){throw 'RemovalBlocked'}
    for($current=$full;$current;$current=[IO.Path]::GetDirectoryName($current)){
        if((Test-Path -LiteralPath $current) -and ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint)){throw 'RemovalBlocked'}
    }
    $full
}

function Get-IsafomDriverPaths($Drivers,[bool]$Strict=$false){
    foreach($driver in $Drivers){foreach($key in @('DriverPath','ConfigFile','DataFile','HelpFile','DependentFiles')){
        if($null -eq $driver.PSObject.Properties[$key]){continue}
        foreach($file in $driver.$key){
            if(-not $file){continue}
            $full=[IO.Path]::GetFullPath([string]$file)
            # Driver Store paths belong to PnPUtil; only direct spool copies are candidates here.
            if($full.StartsWith((Join-Path $env:WINDIR 'System32\spool\DRIVERS\'),[StringComparison]::OrdinalIgnoreCase)){
                try{Test-IsafomDriverFilePath $full}catch{if($Strict){throw}}
            }
        }
    }}
}

function Get-IsafomRecoveredFiles {
    $path=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CNR-ISAFOM\removal-files.json'
    if(-not [IO.File]::Exists($path)){return}
    if(([IO.FileInfo]$path).Length -gt 1MB -or ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'Configuration'}
    $rows=[IO.File]::ReadAllText($path)|ConvertFrom-Json
    if($null -eq $rows){return};$rows=@($rows)
    if($rows.Count -gt 2048){throw 'Configuration'}
    foreach($row in $rows){
        if($row.Sha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Configuration'}
        $full=Test-IsafomDriverFilePath ([string]$row.Path)
        if([IO.File]::Exists($full)){
            if((Get-IsafomFileHash $full) -cne $row.Sha256){throw 'RemovalChanged'}
            [pscustomobject]@{Path=$full;Sha256=[string]$row.Sha256}
        }
    }
}

function Get-IsafomKnownDriverFiles {$script:IsafomKnownDriverFiles}

function Get-IsafomRemovalFileFacts($AllDrivers,$Registrations){
    $selected=@($AllDrivers|Where-Object {$d=$_;@($Registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count -gt 0})
    $others=@($AllDrivers|Where-Object {$d=$_;@($Registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count -eq 0})
    $paths=@(Get-IsafomDriverPaths $selected $true|Sort-Object -Unique)
    $files=@($paths|Where-Object {[IO.File]::Exists($_)}|ForEach-Object {[pscustomobject]@{Path=$_;Sha256=(Get-IsafomFileHash $_)}})
    $files+=@(Get-IsafomRecoveredFiles)
    $unknown=@()
    # Recover exact original 8.5/8.7 spool copies even if a previous uninstall
    # already removed their registrations/INF. Hashes come from official bytes.
    foreach($known in @(Get-IsafomKnownDriverFiles)){
        if($known.Name -notmatch '^[A-Za-z0-9_. ()-]{1,128}$'){throw 'Configuration'}
        $path=Test-IsafomDriverFilePath (Join-Path $env:WINDIR ('System32\spool\DRIVERS\x64\3\'+$known.Name))
        if(-not [IO.File]::Exists($path) -or @($files|Where-Object Path -IEQ $path).Count){continue}
        $hash=Get-IsafomFileHash $path
        if($hash -cin $known.Hashes){$files+=@([pscustomobject]@{Path=$path;Sha256=$hash})}
        else{$unknown+=@('File Kyocera non riconosciuto: '+$known.Name)}
    }
    $files=@($files|Sort-Object Path -Unique)
    if($files.Count -gt 2048){throw 'RemovalBlocked'}
    $shared=@(foreach($driver in $others){
        $references=@(Get-IsafomDriverPaths @($driver))
        if(@($files|Where-Object {$_.Path -iin $references}).Count){'File condivisi: '+$driver.Name}
    })
    [pscustomobject]@{Files=$files;SharedBy=@($shared)+@($unknown)}
}

function Save-IsafomRemovalFiles($Files){
    if($Files.Count -eq 0){return}
    $path=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CNR-ISAFOM\removal-files.json'
    # Parent/previous manifest ACLs were validated by the elevated C# helper.
    [IO.File]::WriteAllText($path,(ConvertTo-Json -InputObject @($Files) -Depth 4),[Text.UTF8Encoding]::new($false))
}

function Remove-IsafomDriverRegistration([string]$Name,[string]$PrinterEnvironment){
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
    if(-not ('IsafomNativeDriverRemoval' -as [type])){
        Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class IsafomNativeDriverRemoval {
 [DllImport("winspool.drv",EntryPoint="DeletePrinterDriverExW",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]public static extern bool Delete(string server,string environment,string name,uint flags,uint version);
}
'@
    }
    # Registration only: bypass the vendor file-deletion callback that stalled
    # the real test. PnPUtil and the bounded, hash-bound file pass follow below.
    if(-not [IsafomNativeDriverRemoval]::Delete($null,$PrinterEnvironment,$Name,0,0)){throw 'RemovalBlocked'}
}

function Remove-IsafomDriverFiles($Files,[bool]$NativeReferences=$false){
    $remaining=0;$restart=$false
    foreach($file in $Files){
        $path=Test-IsafomDriverFilePath ([string]$file.Path)
        if(-not [IO.File]::Exists($path)){continue}
        $used=if($NativeReferences){@([IsafomDriverLocks]::ReferencedFiles())}else{@(Get-IsafomDriverPaths @(Get-PrinterDriver))}
        $usedNames=@($used|ForEach-Object {[IO.Path]::GetFileName($_)})
        if($path -iin $used -or [IO.Path]::GetFileName($path) -iin $usedNames -or (Get-IsafomFileHash $path) -cne $file.Sha256){$remaining++;continue}
        try{[IO.File]::Delete($path)}catch{$restart=$true}
        if([IO.File]::Exists($path)){$remaining++}
    }
    [pscustomobject]@{Complete=($remaining -eq 0);Remaining=$remaining;RestartRequired=$restart}
}

function Complete-IsafomRemovalFiles {
    # Invoked in a NEW backend after the WMI client used for unregistering has
    # exited. Native inventory preserves shared-file protection without keeping
    # the old provider's vendor DLL references alive. WMI itself is never stopped.
    Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
    if(-not ('IsafomDriverLocks' -as [type])){Add-Type -TypeDefinition $script:IsafomDriverLocksSource -ErrorAction Stop}
    $files=@(Get-IsafomRecoveredFiles)
    $deadline=[DateTime]::UtcNow.AddSeconds(75)
    do{
        $result=Remove-IsafomDriverFiles $files $true
        if($result.Complete -or -not $result.RestartRequired -or [DateTime]::UtcNow -ge $deadline){return $result}
        Start-Sleep -Seconds 5
    }while($true)
}

function Restart-IsafomEmptySpooler {
    # The original user explicitly approved this conditional system-wide step.
    # Unknown/queued jobs or dependent services prevent the restart; never force.
    try{
        foreach($queue in @(Get-Printer)){
            if(@(Get-PrintJob -PrinterName $queue.Name -ErrorAction Stop).Count -gt 0){return $false}
        }
        Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1') -ErrorAction Stop
        Restart-Service -Name Spooler -ErrorAction Stop
        (Get-Service -Name Spooler).WaitForStatus('Running',[TimeSpan]::FromSeconds(45))
        return $true
    }catch{return $false}
}

function Release-IsafomIsolationHosts($Files){
    try{
        foreach($queue in @(Get-Printer)){
            if(@(Get-PrintJob -PrinterName $queue.Name -ErrorAction Stop).Count -gt 0){return -1}
        }
        Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
        if(-not ('IsafomDriverLocks' -as [type])){Add-Type -TypeDefinition $script:IsafomDriverLocksSource -ErrorAction Stop}
        $existing=@($Files|Where-Object {[IO.File]::Exists($_.Path)})
        $paths=[string[]]@($existing|ForEach-Object Path)
        $hashes=[string[]]@($existing|ForEach-Object Sha256)
        return [IsafomDriverLocks]::Release($paths,$hashes)
    }catch{return -1}
}

function Test-IsafomOriginalDefault([string]$OwnerSid,[string]$QueueName) {
    $key=$null
    try {
        $key=[Microsoft.Win32.Registry]::Users.OpenSubKey($OwnerSid+'\Software\Microsoft\Windows NT\CurrentVersion\Windows',$false)
        if($null -eq $key){return $false}
        $device=$key.GetValue('Device',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return ($device -is [string] -and $device.StartsWith($QueueName+',',[StringComparison]::OrdinalIgnoreCase))
    } finally {if($null -ne $key){$key.Dispose()}}
}

function Get-IsafomPackageFacts {param($s,$allDrivers)
    if($s.DriverName -cne 'Kyocera TASKalfa 3554ci KX'){throw 'Configuration'}
    $folder=Join-Path $env:WINDIR 'INF'
    $packages=@(Get-ChildItem -LiteralPath $folder -Filter 'oem*.inf' -File|Sort-Object Name|ForEach-Object {
        if($_.Name -notmatch '^oem[0-9]+\.inf$' -or $_.Length -gt 4MB -or $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){return}
        $text=[IO.File]::ReadAllText($_.FullName)
        if($text.IndexOf('"'+$s.DriverName+'"',[StringComparison]::OrdinalIgnoreCase) -lt 0 -or
            $text -notmatch '(?im)^\s*Class\s*=\s*Printer\s*$' -or $text -notmatch '(?i)KYOCERA'){return}
        [pscustomobject]@{Name=$_.Name;Sha256=(Get-IsafomFileHash $_.FullName)}
    })
    $names=@();$registrations=@();$unresolved=$false
    foreach($driver in $allDrivers){
        $path=[string]$driver.InfPath
        if(-not [IO.File]::Exists($path)){if($driver.Name -ceq $s.DriverName){$unresolved=$true};continue}
        $hash=Get-IsafomFileHash $path
        if(@($packages|Where-Object Sha256 -CEQ $hash).Count -gt 0){
            $names+=@([string]$driver.Name)
            $registrations+=@([pscustomobject]@{Name=[string]$driver.Name;Environment=[string]$driver.PrinterEnvironment})
        }
        elseif($driver.Name -ceq $s.DriverName){$unresolved=$true}
    }
    [pscustomobject]@{Packages=$packages;DriverNames=@($names|Sort-Object -Unique);Registrations=@($registrations|Sort-Object Name,Environment);Unresolved=$unresolved}
}

function Get-IsafomRemovalStorage {param([string]$OwnerSid,[string[]]$QueueNames)
    $root=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CNR-ISAFOM'
    $preferences=$false
    foreach($leaf in @('DevModePerUser','DevModes2')){
        $key=$null
        try{
            $key=[Microsoft.Win32.Registry]::Users.OpenSubKey($OwnerSid+'\Printers\'+$leaf,$false)
            if($null -ne $key){foreach($name in $QueueNames){if($key.GetValueNames() -ccontains $name){$preferences=$true}}}
        }finally{if($null -ne $key){$key.Dispose()}}
    }
    [pscustomobject]@{CachePresent=((Test-Path -LiteralPath (Join-Path $root 'driver-kx85-cache')) -or (Test-Path -LiteralPath (Join-Path $root 'driver-cache')) -or (Test-Path -LiteralPath (Join-Path $root 'removal-files.json')));
        SettingsPresent=(Test-Path -LiteralPath (Join-Path $root 'printer-settings.json'));PreferencesPresent=$preferences}
}

function Get-IsafomBoundDevices {param($Packages,[string[]]$QueueName)
    foreach($package in $Packages){
        if($package.Name -cnotmatch '^oem[0-9]+\.inf$'){throw 'Protocol'}
        foreach($device in @(Get-CimInstance Win32_PnPSignedDriver -Filter ("InfName='"+$package.Name+"'"))){
            if($device.DeviceName -cin @($QueueName) -and $device.DeviceID -match '^SWD\\PRINTENUM\\'){continue}
            [string]$device.DeviceName
        }
    }
}

function Get-IsafomPackagesForDrivers($Drivers){
    $hashes=@();$unresolved=$false
    foreach($driver in $Drivers){
        if($null -eq $driver.PSObject.Properties['InfPath']){$unresolved=$true;continue}
        $path=[string]$driver.InfPath
        if(-not [IO.File]::Exists($path)){$unresolved=$true;continue}
        $hashes+=@(Get-IsafomFileHash $path)
    }
    $packages=@(Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'INF') -Filter 'oem*.inf' -File|Sort-Object Name|ForEach-Object {
        if($_.Name -notmatch '^oem[0-9]+\.inf$' -or $_.Length -gt 4MB -or $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){return}
        $hash=Get-IsafomFileHash $_.FullName
        if($hash -cin $hashes){[pscustomobject]@{Name=$_.Name;Sha256=$hash}}
    })
    foreach($hash in $hashes){if($hash -cnotin @($packages|ForEach-Object Sha256)){$unresolved=$true}}
    [pscustomobject]@{Packages=$packages;Unresolved=$unresolved}
}

function Get-IsafomExtraPortFacts([string[]]$Names){
    foreach($port in @(Get-PrinterPort)){
        if($port.Name -cnotin $Names){continue}
        if($null -eq $port.PSObject.Properties['PortMonitor'] -or -not $port.PortMonitor){throw 'RemovalBlocked'}
        [pscustomobject]@{Name=$port.Name;Host='';Protocol=0;PortNumber=0;Monitor=[string]$port.PortMonitor}
    }
}

function Get-IsafomForcedScope($s,$AllDrivers,$Seed){
    $selected=@($AllDrivers|Where-Object {$d=$_;@($Seed.Registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count -gt 0})
    $unresolved=$Seed.Unresolved;$packages=@($Seed.Packages)
    for($iteration=0;$iteration -lt 128;$iteration++){
        $registrations=@($selected|Sort-Object Name,PrinterEnvironment|ForEach-Object {[pscustomobject]@{Name=$_.Name;Environment=$_.PrinterEnvironment}})
        $facts=Get-IsafomRemovalFileFacts $AllDrivers $registrations
        $selectedNames=@($selected|ForEach-Object Name)
        $add=@($AllDrivers|Where-Object {
            $d=$_
            if(@($registrations|Where-Object {$_.Name -ceq $d.Name -and $_.Environment -ceq $d.PrinterEnvironment}).Count){return $false}
            if($d.Name -notmatch '^Kyocera .+ KX$'){return $false}
            if($d.Name -cin $selectedNames){return $true}
            $references=@(Get-IsafomDriverPaths @($d))
            @($facts.Files|Where-Object {$_.Path -iin $references}).Count -gt 0
        })
        if($add.Count -eq 0){
            return [pscustomobject]@{Packages=$packages;Registrations=$registrations;DriverNames=@($selectedNames|Sort-Object -Unique);Unresolved=$unresolved;FileFacts=$facts}
        }
        $selected+=@($add)
        if($selected.Count -gt 128){throw 'RemovalBlocked'}
        $extra=Get-IsafomPackagesForDrivers $selected
        $packages=@(@($packages)+@($extra.Packages)|Sort-Object Name -Unique)
        $unresolved=$unresolved -or $extra.Unresolved
    }
    throw 'RemovalBlocked'
}

function Get-IsafomRemovalPlan {param($s,[string]$OwnerSid,[bool]$ForceRemoval=$false)
    $state=Get-IsafomSnapshot $s $OwnerSid
    if(-not $state.SpoolerRunning){throw 'SpoolerUnavailable'}
    $all=@(Get-Printer);$allDrivers=@(Get-PrinterDriver)
    $candidates=@($all|Where-Object {
        $q=$_
        $q.Name -ceq $s.PrinterName -or ($q.DriverName -ceq $s.DriverName -and
            @($state.Ports|Where-Object { $_.Name -ceq $q.PortName -and (Test-IsafomTarget $s $_.Host) }).Count -gt 0)
    })
    $blocked=$null;$queue=$null;$manual=$false;$jobs=0;$isDefault=$false;$portName=$null
    if($candidates.Count -gt 1 -and -not $ForceRemoval){$blocked='AmbiguousQueue'}
    if($candidates.Count -eq 1){
        $q=$candidates[0];$queue=$q.Name
        $ports=@($state.Ports|Where-Object { $_.Name -ceq $q.PortName -and (Test-IsafomTarget $s $_.Host) -and $_.Protocol -eq 1 -and $_.PortNumber -eq $s.PortNumber })
        if(($q.Shared -and -not $ForceRemoval) -or $q.Type.ToString() -ne 'Local' -or $q.DriverName -cne $s.DriverName -or ($ports.Count -ne 1 -and -not $ForceRemoval)){$blocked='QueueConflict'}
        else{$portName=$q.PortName}
        $managed=Test-IsafomManagedQueue $q.Name $q.Comment (Get-IsafomOwnerMarker $OwnerSid)
        $manual=-not $managed
        if(-not $ForceRemoval -and -not $managed -and ($q.Comment -match '^CNR-ISAFOM Printer Installer' -or
            $q.Comment -ceq 'Autore: Andrea Esposito - andrea.esposito@cnr.it' -or $q.Name -cne $s.PrinterName)){$blocked='QueueConflict'}
        try{$jobs=@(Get-PrintJob -PrinterName $queue).Count}catch{$jobs=-1}
        if($jobs -ne 0){$blocked='ExistingJobs'}
        $isDefault=Test-IsafomOriginalDefault $OwnerSid $queue
    }else{
        $ports=@($state.Ports|Where-Object Name -CEQ $s.PortName)
        if($ports.Count -eq 1){
            if((Test-IsafomTarget $s $ports[0].Host) -and $ports[0].Protocol -eq 1 -and $ports[0].PortNumber -eq $s.PortNumber){$portName=$ports[0].Name}
            else{$blocked='PortConflict'}
        }
    }
    $driver=@($allDrivers|Where-Object { $_.Name -ceq $s.DriverName -and $_.PrinterEnvironment -ceq 'Windows x64' })
    $version=$null
    if($driver.Count -gt 1){$blocked='DriverConflict'}
    if($driver.Count -eq 1){
        $version='non rilevata'
        if($null -ne $driver[0].PSObject.Properties['DriverVersion']){
            $v=[uint64]$driver[0].DriverVersion
            $version='{0}.{1}.{2}.{3}' -f (($v -shr 48) -band 65535),(($v -shr 32) -band 65535),(($v -shr 16) -band 65535),($v -band 65535)
        }
    }
    $package=Get-IsafomPackageFacts $s $allDrivers
    if($ForceRemoval){$package=Get-IsafomForcedScope $s $allDrivers $package;$fileFacts=$package.FileFacts}
    else{$fileFacts=Get-IsafomRemovalFileFacts $allDrivers $package.Registrations}
    if($package.Unresolved){$blocked='RemovalBlocked'}
    $modelNames=@($package.DriverNames)
    $removeQueues=@($all|Where-Object {$_.Name -ceq $queue -or ($ForceRemoval -and $_.DriverName -cin $modelNames)}|Sort-Object Name)
    $queueFacts=@(foreach($q in $removeQueues){
        $count=0;try{$count=@(Get-PrintJob -PrinterName $q.Name).Count}catch{$count=-1}
        if($q.Type.ToString() -ne 'Local'){$blocked='QueueConflict'}
        if($count -ne 0){$blocked='ExistingJobs'}
        $default=Test-IsafomOriginalDefault $OwnerSid $q.Name
        if($default){$isDefault=$true}
        [pscustomobject]@{Name=$q.Name;DriverName=$q.DriverName;PortName=$q.PortName;Jobs=$count;Shared=[bool]$q.Shared;IsDefault=$default}
    })
    $removeNames=@($queueFacts|ForEach-Object Name)
    $jobs=0;foreach($fact in $queueFacts){if($fact.Jobs -lt 0){$jobs=-1;break};$jobs+=$fact.Jobs}
    $driverUsers=@($all|Where-Object { ($_.DriverName -ceq $s.DriverName -or $_.DriverName -cin $modelNames) -and $_.Name -cnotin $removeNames }|Sort-Object Name|ForEach-Object {$_.Name})
    $driverUsers+=@(Get-IsafomBoundDevices $package.Packages $removeNames|ForEach-Object {'Dispositivo: '+$_})
    $driverUsers+=@($fileFacts.SharedBy)
    $queuePorts=@($queueFacts|ForEach-Object PortName)
    $removePorts=@($state.Ports|Where-Object {$_.Name -ceq $portName -or (Test-IsafomTarget $s $_.Host) -or ($ForceRemoval -and $_.Name -cin $queuePorts)}|Sort-Object Name|ForEach-Object {
        [pscustomobject]@{Name=$_.Name;Host=$_.Host;Protocol=[int]$_.Protocol;PortNumber=[int]$_.PortNumber}
    })
    if($ForceRemoval){
        $tcpNames=@($removePorts|ForEach-Object Name)
        $otherNames=@($queuePorts|Where-Object {$_ -cnotin $tcpNames}|Sort-Object -Unique)
        if($otherNames.Count){$removePorts+=@(Get-IsafomExtraPortFacts $otherNames)}
    }
    $removePorts=@($removePorts|Sort-Object Name -Unique)
    $portNames=@($removePorts|ForEach-Object Name)
    $portUsers=@($all|Where-Object {$_.PortName -cin $portNames -and $_.Name -cnotin $removeNames}|Sort-Object Name|ForEach-Object {$_.Name})
    if($driverUsers.Count -gt 0 -or $portUsers.Count -gt 0){$blocked='RemovalShared'}
    $preferenceNames=@(@($s.PrinterName)+@($removeNames)|Where-Object {$null -ne $_}|Sort-Object -Unique)
    $storage=Get-IsafomRemovalStorage $OwnerSid $preferenceNames
    $hasWork=$queueFacts.Count -gt 0 -or $driver.Count -gt 0 -or $package.Packages.Count -gt 0 -or $removePorts.Count -gt 0 -or
        $storage.CachePresent -or $storage.SettingsPresent -or $storage.PreferencesPresent -or $fileFacts.Files.Count -gt 0
    $plan=[ordered]@{QueueName=$queue;ManualQueue=$manual;Jobs=$jobs;IsDefault=$isDefault;DriverName=$s.DriverName;
        DriverVersion=$version;DriverPresent=($driver.Count -gt 0);DriverRemovable=($driverUsers.Count -eq 0);
        DriverUsedBy=$driverUsers;CanProceed=($null -eq $blocked -and $hasWork);BlockedCode=$blocked;
        PortName=$portName;PortUsedBy=$portUsers;Packages=@($package.Packages);PackageDriverNames=$modelNames;
        DriverRegistrations=@($package.Registrations);DriverFiles=@($fileFacts.Files);ForceRemoval=$ForceRemoval;
        QueuesToRemove=$queueFacts;PortsToRemove=$removePorts;
        PreferenceQueues=$preferenceNames;DownloadCachePresent=$storage.CachePresent;SettingsFilePresent=$storage.SettingsPresent;
        UserPreferencesPresent=$storage.PreferencesPresent;FullRemoval=$true}
    $facts=[ordered]@{Plan=$plan;Candidates=@($candidates|Sort-Object Name|ForEach-Object {
        [ordered]@{Name=$_.Name;Driver=$_.DriverName;Port=$_.PortName;Comment=$_.Comment;Shared=$_.Shared}
    });Owner=(Get-IsafomOwnerMarker $OwnerSid)}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$fingerprint=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($facts|ConvertTo-Json -Depth 9 -Compress)))).Replace('-','')}
    finally{$sha.Dispose()}
    $plan['Fingerprint']=$fingerprint
    $plan
}

function Remove-IsafomOemPackage {param($package,[bool]$ForceRemoval=$false)
    if($package.Name -cnotmatch '^oem[0-9]+\.inf$' -or $package.Sha256 -cnotmatch '^[A-F0-9]{64}$'){throw 'Protocol'}
    $file=Join-Path (Join-Path $env:WINDIR 'INF') $package.Name
    if(-not (Test-Path -LiteralPath $file)){return [pscustomobject]@{Removed=$true;RestartRequired=$false}}
    if((Get-IsafomFileHash $file) -cne $package.Sha256){throw 'RemovalChanged'}
    # /force is limited to the reviewed closure after all queues/driver users
    # and PnP dependencies were rechecked. Never /uninstall or /reboot devices.
    if($ForceRemoval){$null=& (Join-Path $env:WINDIR 'System32\pnputil.exe') /delete-driver $package.Name /force}
    else{$null=& (Join-Path $env:WINDIR 'System32\pnputil.exe') /delete-driver $package.Name}
    $exitCode=$LASTEXITCODE
    [pscustomobject]@{Removed=($exitCode -eq 0 -and -not (Test-Path -LiteralPath $file));RestartRequired=($exitCode -eq 3010)}
}

function Test-IsafomOemPackagePresent($Package){
    if($Package.Name -cnotmatch '^oem[0-9]+\.inf$'){throw 'Protocol'}
    Test-Path -LiteralPath (Join-Path $env:WINDIR ('INF\'+$Package.Name))
}

function Invoke-IsafomRemoval {param($s,[string]$OwnerSid,[string]$Fingerprint,[bool]$RemoveDriver,[bool]$AllowSpoolerRestart=$false,[bool]$ForceRemoval=$false)
    if($Fingerprint -cnotmatch '^[A-F0-9]{64}$'){throw 'Protocol'}
    $plan=Get-IsafomRemovalPlan $s $OwnerSid $ForceRemoval
    if($plan.Fingerprint -cne $Fingerprint){throw 'RemovalChanged'}
    if(-not $plan.CanProceed -or -not $RemoveDriver){throw 'RemovalBlocked'}
    $queueRemoved=$false;$restart=$false
    $current=Get-IsafomRemovalPlan $s $OwnerSid $ForceRemoval
    if($current.Fingerprint -cne $Fingerprint -or -not $current.CanProceed){throw 'RemovalChanged'}
    Save-IsafomRemovalFiles @($plan.DriverFiles)
    foreach($q in $plan.QueuesToRemove){
        # Recheck every queue before each deletion, including a job that arrived
        # after a previous queue was removed. Force mode never discards jobs.
        $presentNames=@(Get-Printer|ForEach-Object Name)
        foreach($reviewed in $plan.QueuesToRemove){if($reviewed.Name -cin $presentNames -and @(Get-PrintJob -PrinterName $reviewed.Name -ErrorAction Stop).Count -gt 0){throw 'ExistingJobs'}}
        Remove-Printer -Name $q.Name -ErrorAction Stop
        if(@(Get-Printer|Where-Object Name -CEQ $q.Name).Count -ne 0){throw 'Verification'}
        $queueRemoved=$true
    }
    $driverFailed=$false
    foreach($registration in $plan.DriverRegistrations){
        if(@(Get-Printer|Where-Object {$_.DriverName -cin $plan.PackageDriverNames -or $_.DriverName -ceq $s.DriverName}).Count -gt 0 -or
            @(Get-IsafomBoundDevices $plan.Packages $null).Count -gt 0){$driverFailed=$true;break}
        try{Remove-IsafomDriverRegistration -Name $registration.Name -PrinterEnvironment $registration.Environment}catch{$driverFailed=$true;break}
    }
    $remainingDrivers=@(Get-PrinterDriver)
    $remaining=Get-IsafomPackageFacts $s $remainingDrivers
    $driversAbsent=-not $driverFailed -and $remaining.DriverNames.Count -eq 0 -and
        @($remainingDrivers|Where-Object {$_.Name -cin $plan.PackageDriverNames}).Count -eq 0 -and
        @(Get-PrinterDriver|Where-Object {$_.Name -ceq $s.DriverName -and $_.PrinterEnvironment -ceq 'Windows x64'}).Count -eq 0
    $packageRemoved=$driversAbsent
    if($driversAbsent){
        foreach($package in $plan.Packages){
            if(@(Get-IsafomBoundDevices $plan.Packages $null).Count -gt 0){$packageRemoved=$false;break}
            $result=Remove-IsafomOemPackage $package $ForceRemoval
            $restart=$restart -or $result.RestartRequired
            if(-not $result.Removed){$packageRemoved=$false;break}
        }
        if($packageRemoved){$packageRemoved=(Get-IsafomPackageFacts $s @(Get-PrinterDriver)).Packages.Count -eq 0 -and @($plan.Packages|Where-Object {Test-IsafomOemPackagePresent $_}).Count -eq 0}
    }
    $fileResult=[pscustomobject]@{Complete=$false;Remaining=$plan.DriverFiles.Count;RestartRequired=$false}
    $packageRestartRequired=$restart
    $spoolerRestarted=$false;$isolatedHostsClosed=0
    if($driversAbsent -and $packageRemoved){
        $fileResult=Remove-IsafomDriverFiles @($plan.DriverFiles)
        if(-not $fileResult.Complete -and $fileResult.RestartRequired -and $AllowSpoolerRestart){
            $spoolerRestarted=Restart-IsafomEmptySpooler
            if($spoolerRestarted){$fileResult=Remove-IsafomDriverFiles @($plan.DriverFiles)}
            if(-not $fileResult.Complete){
                $released=Release-IsafomIsolationHosts @($plan.DriverFiles)
                if($released -gt 0){$isolatedHostsClosed=$released;$fileResult=Remove-IsafomDriverFiles @($plan.DriverFiles)}
            }
        }
        $restart=$restart -or $fileResult.RestartRequired
    }
    $portRemoved=$true
    foreach($reviewed in $plan.PortsToRemove){
        if($reviewed.Protocol -eq 0){
            $port=@(Get-IsafomExtraPortFacts @($reviewed.Name))
            if($port.Count -eq 0){continue}
            if($port.Count -ne 1 -or $port[0].Monitor -cne $reviewed.Monitor -or @(Get-Printer|Where-Object PortName -CEQ $reviewed.Name).Count -gt 0){$portRemoved=$false;continue}
            try{
                Remove-PrinterPort -Name $reviewed.Name -ErrorAction Stop
                if(@(Get-IsafomExtraPortFacts @($reviewed.Name)).Count){$portRemoved=$false}
            }catch{$portRemoved=$false}
            continue
        }
        $state=Get-IsafomSnapshot $s $OwnerSid
        $port=@($state.Ports|Where-Object Name -CEQ $reviewed.Name)
        if($port.Count -eq 0){continue}
        if(@(Get-Printer|Where-Object PortName -CEQ $reviewed.Name).Count -eq 0 -and
            $port[0].Host -ceq $reviewed.Host -and $port[0].Protocol -eq $reviewed.Protocol -and $port[0].PortNumber -eq $reviewed.PortNumber){
            try{
                Remove-PrinterPort -Name $reviewed.Name -ErrorAction Stop
                if(@((Get-IsafomSnapshot $s $OwnerSid).Ports|Where-Object Name -CEQ $reviewed.Name).Count -ne 0){$portRemoved=$false}
            }catch{$portRemoved=$false}
        }else{$portRemoved=$false}
    }
    $queueAbsent=@(Get-Printer|Where-Object {$_.Name -cin @($plan.QueuesToRemove|ForEach-Object Name)}).Count -eq 0
    [ordered]@{Ok=$true;Code=$null;Step='Complete';Removal=[ordered]@{QueueRemoved=$queueRemoved;QueueAbsent=$queueAbsent;
        DriverRemoved=$driversAbsent;DriverRetained=(-not $driversAbsent);RequestedDriverRemoval=$true;
        PackageRemoved=$packageRemoved;PortRemoved=$portRemoved;DriverFilesRemoved=$fileResult.Complete;DriverFilesRemaining=$fileResult.Remaining;RestartRequired=$restart;PackageRestartRequired=$packageRestartRequired;SpoolerRestarted=$spoolerRestarted;IsolatedHostsClosed=$isolatedHostsClosed}}
}
