# Selective force removal. No dependency closure, network probe, spooler reset,
# job purge, registry deletion, /force package deletion or global storage cleanup.
function Get-IsafomQueueRegistry {
    $root=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\Print\Printers',$false)
    if($null -eq $root){throw 'RemovalBlocked'}
    try {
        foreach($name in $root.GetSubKeyNames()) {
            $key=$root.OpenSubKey($name,$false)
            if($null -eq $key){throw 'RemovalChanged'}
            try {
                # Never read DEVMODE, personal preferences or driver data blobs.
                [pscustomobject]@{Name=$name;QueueId=[string]$key.GetValue('QueueInstanceId','');
                    DriverName=[string]$key.GetValue('Printer Driver','');PortName=[string]$key.GetValue('Port','');
                    Comment=[string]$key.GetValue('Description','');Attributes=[int]$key.GetValue('Attributes',0)}
            } finally {$key.Dispose()}
        }
    } finally {$root.Dispose()}
}

function Get-IsafomSelectiveHash($Value) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $Value -Depth 12 -Compress)))).Replace('-','')}
    finally {$sha.Dispose()}
}

function Get-IsafomSelectiveEndpoint([string]$Value) {
    $ip=$null
    if([Net.IPAddress]::TryParse($Value,[ref]$ip)){return $ip.ToString().ToLowerInvariant()}
    $Value.Trim().TrimEnd('.').ToLowerInvariant()
}

function Get-IsafomSelectiveDriverNames {
    Get-PrinterDriver -ErrorAction Stop|ForEach-Object Name
}

function Get-IsafomSelectiveInventory($s,[string]$OwnerSid) {
    if((Get-Service Spooler -ErrorAction Stop).Status -ne 'Running'){throw 'SpoolerUnavailable'}
    $queues=@(Get-Printer -ErrorAction Stop)
    $registry=@(Get-IsafomQueueRegistry)
    $ports=@(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction Stop)
    $portInventoryKnown=$true;$installedPorts=@()
    try {$installedPorts=@(Get-PrinterPort -ErrorAction Stop|ForEach-Object Name)} catch {$portInventoryKnown=$false}
    $driverInventoryKnown=$true;$installedDrivers=@()
    try {$installedDrivers=@(Get-IsafomSelectiveDriverNames)} catch {$driverInventoryKnown=$false}
    $marker=Get-IsafomOwnerMarker $OwnerSid
    # Include damaged registrations absent from the PrintManagement provider.
    $names=@(@($queues|ForEach-Object Name)+@($registry|ForEach-Object Name)|Sort-Object -Unique)
    $rows=@(foreach($name in $names) {
        $q=@($queues|Where-Object Name -CEQ $name)
        $r=@($registry|Where-Object Name -CEQ $name)
        $id='';$guid=[guid]::Empty
        if($r.Count -eq 1 -and [guid]::TryParse($r[0].QueueId,[ref]$guid) -and $guid -ne [guid]::Empty){$id=$guid.ToString('D')}
        $driver='';$port='';$comment='';$local=$false;$shared=$false;$status='Registrazione incompleta';$object=$null
        if($q.Count -eq 1) {
            $object=$q[0];$driver=[string]$object.DriverName;$port=[string]$object.PortName;$comment=[string]$object.Comment
            $local=$object.Type.ToString() -eq 'Local';$shared=[bool]$object.Shared;$status=[string]$object.PrinterStatus
        } elseif($q.Count -eq 0 -and $r.Count -eq 1) {
            $driver=$r[0].DriverName;$port=$r[0].PortName;$comment=$r[0].Comment
            $local=($r[0].Attributes -band 64) -ne 0;$shared=($r[0].Attributes -band 8) -ne 0
        }
        $portNames=@($port -split ','|ForEach-Object {$_.Trim()}|Where-Object {$_})
        $tcp=@($ports|Where-Object {$_.Name -iin $portNames}|Sort-Object Name)
        $address=(@($tcp|ForEach-Object HostAddress) -join ', ')
        $owned=Test-IsafomManagedQueue $name $comment $marker
        $target=$tcp.Count -eq 1 -and (Test-IsafomTarget $s $tcp[0].HostAddress)
        $associated=$owned -or ($driver -ceq $s.DriverName -and $target)
        $kyocera=$driver -match '^Kyocera(?:\s|$)'
        $association=if($owned){'ISAFOM'}elseif($associated){'ISAFOM: duplicata/manuale o incompleta'}elseif($kyocera){'Altra Kyocera — associazione ISAFOM non verificata'}else{'Altra stampante — associazione ISAFOM non verificata'}
        $incomplete=$q.Count -eq 0 -or -not $driver.Trim() -or $portNames.Count -eq 0 -or $comment -ceq ($marker+' pending') -or
            ($driverInventoryKnown -and $driver -notin $installedDrivers) -or
            ($portInventoryKnown -and @($portNames|Where-Object {$_ -notin $installedPorts}).Count -gt 0)
        $default=Test-IsafomOriginalDefault $OwnerSid $name
        $protection=''
        if($driver -match '^(Microsoft (Print to PDF|XPS Document Writer|Shared Fax Driver)(\s|$)|Remote Desktop |OneNote|Send to (Microsoft )?OneNote|Fax\b)'){$protection='Stampante di sistema'}
        elseif(-not $local -or @($portNames|Where-Object {$_ -match '^TS[0-9]+$'}).Count){$protection='Connessione remota protetta'}
        elseif($shared){$protection='Coda condivisa con altri utenti'}
        elseif(($comment -match '^CNR-ISAFOM Printer Installer' -or $comment -ceq 'Autore: Andrea Esposito - andrea.esposito@cnr.it') -and -not $owned){$protection='Installazione di un altro utente o proprietà non verificata'}
        elseif(-not $id -or $q.Count -gt 1 -or $r.Count -ne 1){$protection='Identità Windows non univoca o non disponibile'}
        elseif($q.Count -eq 1 -and ($r[0].DriverName -cne $driver -or $r[0].PortName -cne $port)){$protection='Inventario incoerente: aggiornare'}
        $signature=Get-IsafomSelectiveHash @($id,$name,$driver,$port,$address,$comment,$local,$shared,$default)
        [pscustomobject]@{QueueId=$id;Name=$name;DriverName=$driver;PortName=$port;Address=$address;Status=$status;
            Association=$association;Associated=$associated;Protection=$protection;CanSelect=$false;
            Reasons=@(if($associated){'Installazione ISAFOM'};if($kyocera){'Driver Kyocera'};if($incomplete){'Configurazione incompleta'});
            Signature=$signature;IsDefault=$default;Object=$object;IdentityComment=$comment}
    })
    # Relationships come only from original ISAFOM/Kyocera registrations and
    # configured installer endpoints. Never traverse newly related queues.
    $references=@($rows|Where-Object {$_.Associated -or $_.DriverName -match '^Kyocera(?:\s|$)'})
    $referencePorts=@(@($s.PortName)+@($references|ForEach-Object {$_.PortName -split ','|ForEach-Object {$_.Trim()}|Where-Object {$_}})|Sort-Object -Unique)
    $referenceDrivers=@(@($s.DriverName)+@($references|ForEach-Object DriverName|Where-Object {$_})|Sort-Object -Unique)
    $referenceEndpoints=@(@($s.Host,$s.FallbackIPv4)+@($ports|Where-Object {$_.Name -iin $referencePorts}|ForEach-Object HostAddress)|Where-Object {$_}|ForEach-Object {Get-IsafomSelectiveEndpoint $_}|Sort-Object -Unique)
    foreach($row in $rows) {
        $portNames=@($row.PortName -split ','|ForEach-Object {$_.Trim()}|Where-Object {$_})
        if(@($portNames|Where-Object {$_ -iin $referencePorts}).Count){$row.Reasons+=@('Stessa porta della Kyocera')}
        if(@($ports|Where-Object {$_.Name -iin $portNames}|Where-Object {(Get-IsafomSelectiveEndpoint $_.HostAddress) -cin $referenceEndpoints}).Count){$row.Reasons+=@('Stesso IP/host della Kyocera')}
        if($row.DriverName -and $row.DriverName -iin $referenceDrivers){$row.Reasons+=@('Driver condiviso con la Kyocera')}
        if(-not $row.Protection -and $row.Reasons.Count -eq 0){$row.Protection='Nessun collegamento con la Kyocera e nessuna configurazione incompleta'}
        if($row.QueueId -and @($rows|Where-Object QueueId -CEQ $row.QueueId).Count -ne 1){$row.CanSelect=$false;$row.Protection='GUID Windows duplicato'}
        $row.CanSelect=$row.Protection.Length -eq 0
    }
    $rows
}

function Get-IsafomSelectiveResources($s,$Rows,$Selected) {
    $ids=@($Selected|ForEach-Object QueueId)
    $others=@($Rows|Where-Object {$_.QueueId -cnotin $ids -or -not $_.QueueId})
    if(@($others|Where-Object {-not $_.DriverName -or -not $_.PortName}).Count) {
        return [pscustomobject]@{Ports=@();Drivers=@();Packages=@();Files=@();Retained=@();Unresolved=@('Risorse conservate: configurazione di altre code incompleta')}
    }
    $ports=@(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction Stop)
    $drivers=@(Get-PrinterDriver -ErrorAction Stop)
    $removePorts=@();$removeDrivers=@();$packages=@();$files=@();$retained=@();$unresolved=@()
    foreach($name in @($Selected|ForEach-Object PortName|Sort-Object -Unique)) {
        $p=@($ports|Where-Object Name -CEQ $name)
        $used=@($others|Where-Object {Test-IsafomSelectivePortUse $_.PortName $name})
        if($used.Count){$retained+=@("Porta condivisa: $name");continue}
        if($p.Count -ne 1 -or -not (Test-IsafomTarget $s $p[0].HostAddress) -or $p[0].Protocol -ne 1 -or $p[0].PortNumber -ne $s.PortNumber) {
            # Missing ports are already clean. Non-TCP/IP or foreign ports are
            # kept because their ownership cannot be established safely.
            $retained+=@("Porta conservata (provenienza non dimostrata): $name");continue
        }
        $removePorts+=@([pscustomobject]@{Name=$name;Host=[string]$p[0].HostAddress;Protocol=[int]$p[0].Protocol;PortNumber=[int]$p[0].PortNumber})
    }
    foreach($name in @($Selected|ForEach-Object DriverName|Sort-Object -Unique)) {
        if(@($others|Where-Object DriverName -IEQ $name).Count){$retained+=@("Driver condiviso: $name");continue}
        # Only the exact installer model is eligible. Other models are never
        # pulled into the scope, even when they use the same files or package.
        if(-not $name){continue}
        if($name -cne $s.DriverName){$unresolved+=@("Driver conservato (provenienza non dimostrata): $name");continue}
        $d=@($drivers|Where-Object {$_.Name -ceq $name -and $_.PrinterEnvironment -ceq 'Windows x64'})
        if($d.Count -eq 0){continue}
        if($d.Count -ne 1){$unresolved+=@("Driver ambiguo: $name");continue}
        $facts=Get-IsafomPackagesForDrivers $d
        if($facts.Unresolved -or $facts.Packages.Count -eq 0){$unresolved+=@("Pacchetto non identificato: $name");continue}
        if(@($facts.Packages|Where-Object {$_.Sha256 -cnotin @('B711AAE2DFFD78BFCE3D666288F9852D07EA9C94BF8E83DF7974BA2A1DD6F6B7','6B65DAC2A2217707B0B7A6CE69438E1203C3BD49E82C6ECDDCBE1E984427D139')}).Count) {
            $unresolved+=@("Pacchetto non riconosciuto: $name");continue
        }
        if(@(Get-IsafomSelectiveBoundDevices $facts.Packages $ids).Count) {
            $retained+=@("Driver/pacchetto necessario ad altri dispositivi: $name");continue
        }
        $driverSignature=Get-IsafomSelectiveHash @($d[0].Name,$d[0].PrinterEnvironment,[string]$d[0].InfPath,@($facts.Packages|ForEach-Object Sha256))
        $removeDrivers+=@([pscustomobject]@{Name=$name;Environment='Windows x64';Signature=$driverSignature})
        $fileInventoryUncertain=$false
        foreach($package in $facts.Packages) {
            $otherDrivers=@($drivers|Where-Object {$_.Name -cne $name -or $_.PrinterEnvironment -cne 'Windows x64'})
            $unknown=$false;$used=$false
            foreach($other in $otherDrivers) {
                if(-not $other.InfPath -or -not [IO.File]::Exists($other.InfPath)){$unknown=$true;continue}
                if((Get-IsafomFileHash $other.InfPath) -ceq $package.Sha256){$used=$true}
            }
            if($unknown -or $used) {
                $fileInventoryUncertain=$fileInventoryUncertain -or $unknown
                $retained+=@('Pacchetto condiviso o dipendenze non risolte: '+$package.Name);continue
            }
            $packages+=@($package)
        }
        $otherPaths=@(Get-IsafomDriverPaths @($drivers|Where-Object {$_.Name -cne $name -or $_.PrinterEnvironment -cne 'Windows x64'}))
        $otherLeaves=@($otherPaths|ForEach-Object {[IO.Path]::GetFileName($_)})
        foreach($path in @(Get-IsafomDriverPaths $d $true|Sort-Object -Unique)) {
            if(-not [IO.File]::Exists($path)){continue}
            if($fileInventoryUncertain){$unresolved+=@('File conservato: dipendenze non verificabili: '+$path);continue}
            if($path -iin $otherPaths -or [IO.Path]::GetFileName($path) -iin $otherLeaves){$retained+=@('File condiviso: '+$path);continue}
            $hash=Get-IsafomFileHash $path
            $known=@(Get-IsafomKnownDriverFiles|Where-Object {$_.Name -ieq [IO.Path]::GetFileName($path) -and $hash -cin $_.Hashes})
            if($known.Count -eq 0){$unresolved+=@('File non riconosciuto: '+$path);continue}
            $files+=@([pscustomobject]@{Path=$path;Sha256=$hash})
        }
    }
    [pscustomobject]@{Ports=@($removePorts);Drivers=@($removeDrivers);Packages=@($packages|Sort-Object Name -Unique);Files=@($files|Sort-Object Path -Unique);Retained=@($retained);Unresolved=@($unresolved)}
}

function Get-IsafomSelectiveBoundDevices($Packages,[string[]]$SelectedIds) {
    foreach($package in $Packages) {
        if($package.Name -cnotmatch '^oem[0-9]+\.inf$'){throw 'Protocol'}
        foreach($device in @(Get-CimInstance Win32_PnPSignedDriver -Filter ("InfName='"+$package.Name+"'") -ErrorAction Stop)) {
            $id=[guid]::Empty
            if($device.DeviceID -match '^SWD\\PRINTENUM\\(\{[A-Fa-f0-9-]+\})$' -and [guid]::TryParse($Matches[1],[ref]$id) -and $id.ToString('D') -cin $SelectedIds){continue}
            [string]$device.DeviceID
        }
    }
}

function Get-IsafomSelectivePlan($s,[string]$OwnerSid,[string[]]$QueueIds) {
    if(-not (Test-IsafomSelectiveIds $QueueIds)){throw 'Protocol'}
    $rows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
    $selected=@(foreach($id in $QueueIds) {
        $match=@($rows|Where-Object QueueId -CEQ $id)
        if($match.Count -ne 1 -or -not $match[0].CanSelect){throw 'RemovalChanged'}
        $match[0]
    })
    try {$resources=Get-IsafomSelectiveResources $s $rows $selected}
    catch {$resources=[pscustomobject]@{Ports=@();Drivers=@();Packages=@();Files=@();Retained=@();Unresolved=@('Risorse conservate: inventario delle dipendenze non verificabile')}}
    $facts=[ordered]@{Queues=@($selected|Sort-Object QueueId|Select-Object QueueId,Name,DriverName,PortName,Address,Signature,Status,Association,Associated,Protection,CanSelect,IsDefault,Reasons);
        Ports=$resources.Ports;Drivers=$resources.Drivers;Packages=$resources.Packages;Files=$resources.Files;Retained=$resources.Retained;Unresolved=$resources.Unresolved}
    [pscustomobject]@{Queues=$facts.Queues;Ports=$facts.Ports;Drivers=$facts.Drivers;Packages=$facts.Packages;
        Files=$facts.Files;Retained=$facts.Retained;Unresolved=$facts.Unresolved;Fingerprint=(Get-IsafomSelectiveHash $facts)}
}

function Remove-IsafomSelectiveQueue($Row,[string]$OwnerSid) {
    # A spooler handle remains bound to this instance even if a queue with the
    # same name is subsequently created. Never pass names to wildcard cmdlets.
    Remove-IsafomSelectiveQueueNative $Row $OwnerSid
}

function Assert-IsafomSelectiveDefault($Row,[string]$OwnerSid) {
    # A default queue is removable only when that status was part of the
    # reviewed fingerprint. A later default change requires a new review.
    if([bool](Test-IsafomOriginalDefault $OwnerSid $Row.Name) -ne [bool]$Row.IsDefault){throw 'RemovalChanged'}
}

function Test-IsafomSelectiveApprovedRow($Row,$Approved) {
    # A selected reference queue may already have been removed in this batch.
    # Its loss cannot revoke consent for another unchanged selected instance.
    # All other identity, ownership, system and sharing protections still apply.
    $Approved.CanSelect -and $Row.Signature -ceq $Approved.Signature -and
        ($Row.CanSelect -or $Row.Protection -ceq 'Nessun collegamento con la Kyocera e nessuna configurazione incompleta')
}

function Assert-IsafomSelectiveEndpoint($Row) {
    $portNames=@($Row.PortName -split ','|ForEach-Object {$_.Trim()}|Where-Object {$_})
    $address=(@(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction Stop|Where-Object {$_.Name -iin $portNames}|Sort-Object Name|ForEach-Object HostAddress) -join ', ')
    if($address -cne $Row.Address){throw 'RemovalChanged'}
}

function Remove-IsafomSelectiveQueueNative($Row,[string]$OwnerSid) {
    # Targeted recovery for a damaged PrintManagement registration. OpenPrinter
    # binds a handle; recheck the GUID while that handle is held before Delete.
    if(-not ('IsafomSelectiveNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;using System.ComponentModel;using System.Runtime.InteropServices;
public static class IsafomSelectiveNative {
 [StructLayout(LayoutKind.Sequential)] public struct Defaults {public IntPtr DataType,DevMode;public uint Access;}
 [StructLayout(LayoutKind.Sequential)] public struct Info2 {
  public IntPtr Server,Name,Share,Port,Driver,Comment,Location,DevMode,Separator,Processor,DataType,Parameters,Security;
  public uint Attributes,Priority,DefaultPriority,Start,Until,Status,Jobs,Average;
 }
 [DllImport("winspool.drv",EntryPoint="OpenPrinterW",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)] public static extern bool Open(string name,out IntPtr handle,ref Defaults defaults);
 [DllImport("winspool.drv",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool DeletePrinter(IntPtr handle);
 [DllImport("winspool.drv")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ClosePrinter(IntPtr handle);
 [DllImport("winspool.drv",EntryPoint="GetPrinterW",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetPrinter(IntPtr handle,uint level,IntPtr data,uint size,out uint needed);
 public static bool EmptyAndMatching(IntPtr handle,string name,string driver,string port,string comment) {
  uint size;GetPrinter(handle,2,IntPtr.Zero,0,out size);
  if(size<Marshal.SizeOf(typeof(Info2)) || size>1048576)return false;
  IntPtr data=Marshal.AllocHGlobal((int)size);
  try {
   if(!GetPrinter(handle,2,data,size,out size))return false;
   var info=Marshal.PtrToStructure<Info2>(data);
   return info.Jobs==0 && (info.Attributes & 0x48)==0x40 &&
    String.Equals(Marshal.PtrToStringUni(info.Name)??String.Empty,name,StringComparison.Ordinal) &&
    String.Equals(Marshal.PtrToStringUni(info.Driver)??String.Empty,driver,StringComparison.Ordinal) &&
    String.Equals(Marshal.PtrToStringUni(info.Port)??String.Empty,port,StringComparison.Ordinal) &&
    String.Equals(Marshal.PtrToStringUni(info.Comment)??String.Empty,comment,StringComparison.Ordinal);
  } finally {Marshal.FreeHGlobal(data);}
 }
}
'@ -ErrorAction Stop
    }
    $defaults=New-Object IsafomSelectiveNative+Defaults;$defaults.Access=0x0001000C
    $handle=[IntPtr]::Zero
    if(-not [IsafomSelectiveNative]::Open($Row.Name,[ref]$handle,[ref]$defaults)){throw 'RemovalBlocked'}
    try {
        $r=@(Get-IsafomQueueRegistry|Where-Object Name -CEQ $Row.Name)
        if($r.Count -ne 1 -or ([guid]$r[0].QueueId).ToString('D') -cne $Row.QueueId -or $r[0].DriverName -cne $Row.DriverName -or $r[0].PortName -cne $Row.PortName -or $r[0].Comment -cne $Row.IdentityComment){throw 'RemovalChanged'}
        Assert-IsafomSelectiveDefault $Row $OwnerSid
        Assert-IsafomSelectiveEndpoint $Row
        if(-not [IsafomSelectiveNative]::EmptyAndMatching($handle,$Row.Name,$Row.DriverName,$Row.PortName,$Row.IdentityComment)){throw 'RemovalBlocked'}
        if(-not [IsafomSelectiveNative]::DeletePrinter($handle)){throw 'RemovalBlocked'}
    } finally {$null=[IsafomSelectiveNative]::ClosePrinter($handle)}
}

function Invoke-IsafomSelectiveRemoval($s,[string]$OwnerSid,[string[]]$QueueIds,[string]$Fingerprint) {
    if($Fingerprint -cnotmatch '^[A-F0-9]{64}$' -or -not (Test-IsafomSelectiveIds $QueueIds)){throw 'Protocol'}
    $initial=@(Get-IsafomSelectiveInventory $s $OwnerSid)
    if(@($initial|Where-Object {$_.QueueId -cin $QueueIds}).Count -eq 0) {
        # Replaying an already completed request cannot affect replacement queues.
        return [ordered]@{Ok=$true;SelectiveRemoval=@($QueueIds|ForEach-Object {[ordered]@{QueueId=$_;Name=$_;Outcome='Rimossa parzialmente';Residuals=@('Risorse residue non verificabili con una conferma già eseguita: aggiornare l''inventario');Retained=@();Details='Istanza Windows già assente; nessuna nuova modifica'}})}
    }
    $plan=Get-IsafomSelectivePlan $s $OwnerSid $QueueIds
    if($plan.Fingerprint -cne $Fingerprint){throw 'RemovalChanged'}
    $results=@();$resourceErrors=@{}
    # Delete the selected default last, so Windows cannot promote another
    # selected queue to default halfway through an otherwise approved batch.
    foreach($approved in @($plan.Queues|Sort-Object IsDefault,QueueId)) {
        $removed=$false;$details='';$residuals=@()
        try {
            $rows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
            $matches=@($rows|Where-Object QueueId -CEQ $approved.QueueId)
            if($matches.Count -eq 0){$removed=Test-IsafomSelectiveAbsent $rows $approved}
            elseif($matches.Count -ne 1 -or -not (Test-IsafomSelectiveApprovedRow $matches[0] $approved)){$details='Identità o protezioni cambiate: aggiornare l''elenco'}
            else {
                $row=$matches[0]
                $jobs=@();if($null -ne $row.Object){$jobs=@(Get-PrintJob -PrinterName $row.Name -ErrorAction Stop)}
                if($jobs.Count){$details='Lavori presenti: rimozione bloccata'}
                else {
                    try {Remove-IsafomSelectiveQueue $row $OwnerSid} catch {$details='Comando di rimozione non riuscito'}
                    $after=@(Get-IsafomSelectiveInventory $s $OwnerSid)
                    $present=@($after|Where-Object QueueId -CEQ $approved.QueueId)
                    if($present.Count -eq 1 -and (Test-IsafomSelectiveApprovedRow $present[0] $approved)) {
                        # Bounded retry, same queue only. Never cancel jobs.
                        if($null -eq $present[0].Object -or @(Get-PrintJob -PrinterName $row.Name -ErrorAction Stop).Count -eq 0) {
                            try {Remove-IsafomSelectiveQueueNative $present[0] $OwnerSid} catch {$details='Recupero mirato non riuscito: verificare permessi o coda danneggiata'}
                        }
                    }
                    $after=@(Get-IsafomSelectiveInventory $s $OwnerSid)
                    $removed=Test-IsafomSelectiveAbsent $after $approved
                }
            }
        } catch {$details='Stato non verificabile o permessi insufficienti: nessun ulteriore intervento sulla coda'}
        if(-not $removed){$residuals+=@('Coda ancora presente o assenza non verificata')}
        $results+=@([pscustomobject]@{QueueId=$approved.QueueId;Name=$approved.Name;Outcome='Non rimossa';Residuals=$residuals;Retained=@();Details=$details;Absent=$removed})
    }
    # Recompute dependency safety after queue operations and before EACH mutation.
    # Intersect with the approved plan: a failed queue can only reduce cleanup.
    foreach($port in $plan.Ports) {
        if(@($results|Where-Object {-not $_.Absent -and $_.QueueId -cin @($plan.Queues|Where-Object PortName -CEQ $port.Name|ForEach-Object QueueId)}).Count){continue}
        try {
            $rows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
            if(@($rows|Where-Object {-not $_.PortName}).Count){throw 'RemovalBlocked'}
            if(@($rows|Where-Object {Test-IsafomSelectivePortUse $_.PortName $port.Name}).Count){continue}
            $current=@(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction Stop|Where-Object Name -CEQ $port.Name)
            if($current.Count -eq 0){continue}
            if($current.Count -ne 1 -or $current[0].HostAddress -cne $port.Host -or $current[0].Protocol -ne $port.Protocol -or $current[0].PortNumber -ne $port.PortNumber){throw 'RemovalChanged'}
            try {Remove-PrinterPort -Name $port.Name -ErrorAction Stop} catch { }
            if(@(Get-CimInstance Win32_TCPIPPrinterPort -ErrorAction Stop|Where-Object Name -CEQ $port.Name).Count){throw 'Verification'}
        } catch {$resourceErrors['Port:'+ $port.Name]='Porta residua o non verificabile: '+$port.Name}
    }
    foreach($driver in $plan.Drivers) {
        if(@($results|Where-Object {-not $_.Absent -and $_.QueueId -cin @($plan.Queues|Where-Object DriverName -CEQ $driver.Name|ForEach-Object QueueId)}).Count){continue}
        try {
            $rows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
            if(@($rows|Where-Object {-not $_.DriverName}).Count){throw 'RemovalBlocked'}
            if(@($rows|Where-Object DriverName -IEQ $driver.Name).Count){continue}
            $registration=@(Get-PrinterDriver -ErrorAction Stop|Where-Object {$_.Name -ceq $driver.Name -and $_.PrinterEnvironment -ceq $driver.Environment})
            if($registration.Count) {
                if($registration.Count -ne 1){throw 'RemovalChanged'}
                $facts=Get-IsafomPackagesForDrivers $registration
                $signature=Get-IsafomSelectiveHash @($registration[0].Name,$registration[0].PrinterEnvironment,[string]$registration[0].InfPath,@($facts.Packages|ForEach-Object Sha256))
                if($signature -cne $driver.Signature -or $facts.Unresolved -or @(Get-IsafomSelectiveBoundDevices $facts.Packages @()).Count){throw 'RemovalChanged'}
                try {Remove-IsafomDriverRegistration $driver.Name $driver.Environment} catch { }
            }
            if(@(Get-PrinterDriver -ErrorAction Stop|Where-Object {$_.Name -ceq $driver.Name -and $_.PrinterEnvironment -ceq $driver.Environment}).Count){throw 'Verification'}
        } catch {$resourceErrors['Driver:'+ $driver.Name]='Driver residuo o non verificabile: '+$driver.Name}
    }
    foreach($package in $plan.Packages) {
        try {
            # Include registrations and PnP devices, never just queue names.
            $drivers=@(Get-PrinterDriver -ErrorAction Stop);$unsafe=$false
            foreach($d in $drivers) {
                if(-not $d.InfPath -or -not [IO.File]::Exists($d.InfPath) -or (Get-IsafomFileHash $d.InfPath) -ceq $package.Sha256){$unsafe=$true;break}
            }
            $currentRows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
            if(@($currentRows|Where-Object {-not $_.DriverName}).Count){$unsafe=$true}
            if($unsafe -or @(Get-IsafomSelectiveBoundDevices @($package) @()).Count){$resourceErrors['Package:'+ $package.Name]='Pacchetto conservato per nuove dipendenze: '+$package.Name;continue}
            $packageResult=Remove-IsafomOemPackage $package $false
            if(Test-IsafomOemPackagePresent $package){throw 'Verification'}
            if($packageResult.RestartRequired){$resourceErrors['Package:'+ $package.Name]='Pacchetto: Windows richiede un riavvio prima della verifica completa: '+$package.Name}
        } catch {$resourceErrors['Package:'+ $package.Name]='Pacchetto residuo o non verificabile: '+$package.Name}
    }
    foreach($file in $plan.Files) {
        try {
            $rows=@(Get-IsafomSelectiveInventory $s $OwnerSid)
            if(@($rows|Where-Object {-not $_.DriverName}).Count){throw 'RemovalBlocked'}
            if(@($rows|Where-Object DriverName -IEQ $s.DriverName).Count){continue}
            if(@(Get-PrinterDriver -ErrorAction Stop|Where-Object Name -CEQ $s.DriverName).Count){throw 'RemovalBlocked'}
            foreach($remainingDriver in @(Get-PrinterDriver -ErrorAction Stop)) {
                if(-not $remainingDriver.InfPath -or -not [IO.File]::Exists($remainingDriver.InfPath)){throw 'RemovalBlocked'}
                foreach($field in @('DriverPath','ConfigFile','DataFile','DependentFiles')) {
                    if($null -eq $remainingDriver.PSObject.Properties[$field]){throw 'RemovalBlocked'}
                }
            }
            $fileResult=Remove-IsafomDriverFiles @($file)
            if(-not $fileResult.Complete -or [IO.File]::Exists($file.Path)){throw 'Verification'}
        } catch {$resourceErrors['File:'+ $file.Path]='File residuo o non verificabile: '+$file.Path}
    }
    foreach($result in $results) {
        # Final readback is mandatory, including earlier successful operations.
        $approved=@($plan.Queues|Where-Object QueueId -CEQ $result.QueueId)[0]
        try {$rows=@(Get-IsafomSelectiveInventory $s $OwnerSid);$result.Absent=Test-IsafomSelectiveAbsent $rows $approved}
        catch {$result.Absent=$false;$result.Residuals+=@('Verifica finale non disponibile')}
        if($result.Absent) {
            $result.Details='Assenza della coda verificata nel sistema'
            $result.Residuals=@($result.Residuals|Where-Object {$_ -cne 'Coda ancora presente o assenza non verificata'})
            if($resourceErrors.ContainsKey('Port:'+ $approved.PortName)){$result.Residuals+=@($resourceErrors['Port:'+ $approved.PortName])}
            if($resourceErrors.ContainsKey('Driver:'+ $approved.DriverName)){$result.Residuals+=@($resourceErrors['Driver:'+ $approved.DriverName])}
            if($approved.DriverName -ceq $s.DriverName) {
                $result.Residuals+=@($resourceErrors.Keys|Where-Object { $_.StartsWith('Package:') -or $_.StartsWith('File:') }|ForEach-Object {$resourceErrors[$_]})
            }
            $result.Residuals+=@($plan.Unresolved|Where-Object {
                $_.StartsWith('Risorse ') -or
                ($approved.DriverName -and $_.EndsWith(': '+$approved.DriverName,[StringComparison]::OrdinalIgnoreCase)) -or
                ($approved.DriverName -ieq $s.DriverName -and $_.StartsWith('File '))
            })
            $result.Outcome=if($result.Residuals.Count){'Rimossa parzialmente'}else{'Rimossa completamente'}
        }
        $result.Retained=@($plan.Retained|Where-Object {
            ($approved.PortName -and $_.EndsWith(': '+$approved.PortName,[StringComparison]::OrdinalIgnoreCase)) -or
            ($approved.DriverName -and $_.EndsWith(': '+$approved.DriverName,[StringComparison]::OrdinalIgnoreCase)) -or
            ($approved.DriverName -ieq $s.DriverName -and ($_.StartsWith('Pacchetto ') -or $_.StartsWith('File ')))
        })
        if(@($rows|Where-Object {Test-IsafomSelectivePortUse $_.PortName $approved.PortName}).Count){$result.Retained+=@('Porta ancora utilizzata: '+$approved.PortName)}
        if(@($rows|Where-Object DriverName -IEQ $approved.DriverName).Count){$result.Retained+=@('Driver ancora utilizzato: '+$approved.DriverName)}
    }
    [ordered]@{Ok=$true;SelectiveRemoval=@($results|Select-Object QueueId,Name,Outcome,Residuals,Retained,Details)}
}

function Test-IsafomSelectiveAbsent($Rows,$Approved) {
    @($Rows|Where-Object {$_.QueueId -ceq $Approved.QueueId -or ($_.Name -ceq $Approved.Name -and -not $_.QueueId)}).Count -eq 0
}

function Test-IsafomSelectiveIds([string[]]$QueueIds) {
    $QueueIds.Count -gt 0 -and $QueueIds.Count -le 128 -and @($QueueIds|Sort-Object -Unique).Count -eq $QueueIds.Count -and
        @($QueueIds|Where-Object {$_ -cnotmatch '^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$'}).Count -eq 0
}

function Test-IsafomSelectivePortUse([string]$Ports,[string]$PortName) {
    @($Ports -split ','|ForEach-Object {$_.Trim()}|Where-Object {$_ -ieq $PortName}).Count -gt 0
}
