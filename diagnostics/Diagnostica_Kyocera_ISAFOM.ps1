#requires -Version 5.1
<#
.SYNOPSIS
Diagnostica locale di sola lettura per CNR-ISAFOM Portici.
.DESCRIPTION
Scrive esclusivamente una NUOVA cartella di report e il relativo ZIP.
Non cambia configurazione, non invia stampe, non interroga il dispositivo in rete.
Non legge/esporta campi proprietari, interi hive, PrintTicket o code personali.
Eseguire nella sessione dell'utente che usa la stampante, senza elevazione.
#>
[CmdletBinding()]
param([string]$OutputDirectory = '', [switch]$InMemory, [switch]$ListOnly, [string]$QueueName = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
$expectedDriver = 'Kyocera TASKalfa 3554ci KX'
$expectedHost = 'stampante.isafom.cnr.it'
$expectedIp = '140.164.75.11'

function New-Finding {
    param([object]$Value, [string]$Note = '', [ValidateSet('Verificata','Rilevata indirettamente','Non verificabile')][string]$State = 'Verificata')
    [ordered]@{ State=$State; Value=$Value; Note=$Note }
}
function New-Unknown { param([string]$Note)
    New-Finding -Value $null -Note $Note -State 'Non verificabile'
}

function Read-Property { param($Object,[string]$Name)
    if ($null -ne $Object -and $null -ne $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $null
}
function Get-QueueInventory {
    $map = @{}
    $primaryOk = $false; $fallbackOk = $false
    function Add-Queue { param($Item,[string]$Source)
        $name = [string](Read-Property $Item 'Name')
        if ([string]::IsNullOrWhiteSpace($name)) { return }
        $driver = [string](Read-Property $Item 'DriverName')
        $port = [string](Read-Property $Item 'PortName')
        $shared = Read-Property $Item 'Shared'
        if (-not $map.ContainsKey($name)) {
            $map[$name] = [pscustomobject]@{Name=$name;DriverName=$driver;PortName=$port;Shared=$shared;Source=$Source;ModelHint=($name -match '(?i)3554ci|Kyocera|TASKalfa' -or $driver -match '(?i)3554ci|Kyocera|TASKalfa')}
        }
        else {
            $existing = $map[$name]
            $existing.Source = 'Get-Printer + Win32_Printer'
            if ([string]::IsNullOrWhiteSpace($existing.DriverName)) { $existing.DriverName = $driver }
            if ([string]::IsNullOrWhiteSpace($existing.PortName)) { $existing.PortName = $port }
            # A missing value stays unknown; do not override a successful primary read.
            if ($null -eq $existing.Shared) { $existing.Shared = $shared }
            $existing.ModelHint = ($existing.ModelHint -or $name -match '(?i)3554ci|Kyocera|TASKalfa' -or $driver -match '(?i)3554ci|Kyocera|TASKalfa')
        }
    }
    try { foreach ($q in @(Get-Printer)) { Add-Queue $q 'Get-Printer' }; $primaryOk = $true } catch { }
    try { foreach ($q in @(Get-CimInstance -ClassName Win32_Printer)) { Add-Queue $q 'Win32_Printer' }; $fallbackOk = $true } catch { }
    [pscustomobject]@{Available=($primaryOk -or $fallbackOk);PrimaryAvailable=$primaryOk;FallbackAvailable=$fallbackOk;Queues=@($map.Values | Sort-Object Name)}
}
function Get-DriverFileVersion { param($Driver)
    $path = [string](Read-Property $Driver 'Path')
    if ([string]::IsNullOrWhiteSpace($path)) { $path = [string](Read-Property $Driver 'DriverPath') }
    try {
        # Local DLL only. No path or file contents are exported, no network shares read.
        if (-not [string]::IsNullOrWhiteSpace($path) -and [IO.Path]::IsPathRooted($path) -and -not $path.StartsWith('\\')) {
            $v = (Get-Item -LiteralPath $path).VersionInfo
            return (New-Finding -Value ('{0}.{1}.{2}.{3}' -f $v.FileMajorPart,$v.FileMinorPart,$v.FileBuildPart,$v.FilePrivatePart) -Note 'Versione del file DLL locale: proprietà Path (MSFT_PrinterDriver) o DriverPath (Win32_PrinterDriver).')
        }
    } catch { }
    New-Unknown 'Versione del file driver non leggibile. MajorVersion e Version WMI non sono la versione del prodotto KX.'
}

function Get-DriverPackageFindings { param($Driver)
    $result = [ordered]@{
        RegisteredSpoolerVersion=(New-Unknown 'Versione registrata dello Spooler non leggibile.')
        InfDriverVersion=(New-Unknown 'DriverVer INF locale non leggibile.')
        InfDriverDate=(New-Unknown 'Data DriverVer INF non leggibile; formato originale mese/giorno/anno.')
        MainDllFileVersionText=(New-Unknown 'Stringa FileVersion della DLL principale non leggibile.')
        MainDllProductVersionText=(New-Unknown 'Stringa ProductVersion della DLL principale non leggibile.')
        ConfigDllFileVersionText=(New-Unknown 'Stringa FileVersion della DLL di configurazione non leggibile.')
        ConfigDllProductVersionText=(New-Unknown 'Stringa ProductVersion della DLL di configurazione non leggibile.')
    }
    try {
        $registered = Read-Property $Driver 'DriverVersion'
        if ($null -ne $registered -and [uint64]$registered -gt 0) {
            [uint64]$bits = $registered
            $version = '{0}.{1}.{2}.{3}' -f (($bits -shr 48) -band 65535),(($bits -shr 32) -band 65535),(($bits -shr 16) -band 65535),($bits -band 65535)
            $result.RegisteredSpoolerVersion = New-Finding $version 'DriverVersion MSFT_PrinterDriver: quattro WORD del valore a 64 bit; distinta dalla versione DLL.'
        }
    } catch { }
    try {
        $inf = [string](Read-Property $Driver 'InfPath')
        if ($inf -and [IO.Path]::IsPathRooted($inf) -and -not $inf.StartsWith('\\')) {
            $file = Get-Item -LiteralPath $inf
            if ($file.Length -le 1MB) {
                foreach ($line in Get-Content -LiteralPath $inf) {
                    if ($line -match '^\s*DriverVer\s*=\s*(\d{2}/\d{2}/\d{4})\s*,\s*(\d+\.\d+\.\d+\.\d+)\s*(;.*)?$') {
                        $result.InfDriverDate = New-Finding $Matches[1] 'Data DriverVer nel formato originale INF: mese/giorno/anno.'
                        $result.InfDriverVersion = New-Finding $Matches[2] 'DriverVer dell''INF locale associato; nessun percorso esportato.'
                        break
                    }
                }
            }
        }
    } catch { }
    foreach ($entry in @(@{Property='Path';Prefix='MainDll'},@{Property='ConfigFile';Prefix='ConfigDll'})) {
        try {
            $path = [string](Read-Property $Driver $entry.Property)
            if ($path -and [IO.Path]::IsPathRooted($path) -and -not $path.StartsWith('\\')) {
                $info = (Get-Item -LiteralPath $path).VersionInfo
                foreach ($field in @('FileVersion','ProductVersion')) {
                    $text = [string]$info.$field
                    if ($text -match '^\d{1,6}([.,] *\d{1,6}){3}( \(WinBuild\.\d{6}\.\d{4}\))?$') {
                        $result[($entry.Prefix + $field + 'Text')] = New-Finding $text 'Stringa testuale del file; non è automaticamente la versione del pacchetto.'
                    }
                }
            }
        } catch { }
    }
    $result
}

# Deliberately exposes only the documented DEVMODEW header (color) and presence.
# Private bytes returned by GetPrinter are not interpreted, copied or serialized.
$nativeSource = @'
using System;
using System.Runtime.InteropServices;
public static class IsafomReadOnlySpoolerV1 {
  [DllImport("winspool.drv", CharSet=CharSet.Unicode, SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)] static extern bool OpenPrinter(string name,out IntPtr h,IntPtr defaults);
  [DllImport("winspool.drv",CharSet=CharSet.Unicode,SetLastError=true)]
  [return:MarshalAs(UnmanagedType.Bool)] static extern bool GetPrinter(IntPtr h,uint level,IntPtr p,uint size,out uint needed);
  [DllImport("winspool.drv")]
  [return:MarshalAs(UnmanagedType.Bool)] static extern bool ClosePrinter(IntPtr h);
  // Result: 0=unavailable; 1=DEVMODE present, color unknown; 2=mono; 3=color; 4=no per-user override.
  public static int Read(string name,uint level) {
    if(level!=8 && level!=9) return 0;
    IntPtr h,buffer=IntPtr.Zero; uint size=0;
    if(!OpenPrinter(name,out h,IntPtr.Zero)) return 0;
    try {
      GetPrinter(h,level,IntPtr.Zero,0,out size);
      if(size<IntPtr.Size || size>1048576) return 0;
      buffer=Marshal.AllocHGlobal((int)size);
      uint needed;
      if(!GetPrinter(h,level,buffer,size,out needed)) return 0;
      IntPtr dm=Marshal.ReadIntPtr(buffer);
      if(dm==IntPtr.Zero) return 4;
      long start=buffer.ToInt64(),pos=dm.ToInt64();
      if(pos<start || pos+94>start+size || (ushort)Marshal.ReadInt16(dm,68)<94) return 0;
      if((Marshal.ReadInt32(dm,72)&0x800)==0) return 1;
      short color=Marshal.ReadInt16(dm,92);
      return color==1?2:color==2?3:1;
    }
    finally {
      if(buffer!=IntPtr.Zero) {
        for(int i=0;i<size;i++) Marshal.WriteByte(buffer,i,0);
        Marshal.FreeHGlobal(buffer);
      }
      ClosePrinter(h);
    }
  }
}
'@

function Get-ColorFindings {
    param([string]$QueueName,[uint32]$Level)
    try { $native = [IsafomReadOnlySpoolerV1]::Read($QueueName,$Level) } catch { $native = 0 }
    if ($native -eq 0) {
        return [ordered]@{ DevModePresent=(New-Unknown 'Lettura spooler non disponibile.'); Monochrome=(New-Unknown 'Colore non leggibile.') }
    }
    $present = ($native -ne 4)
    $mono = New-Unknown 'Il driver non espone dmColor nel DEVMODE pubblico.'
    if ($native -eq 2 -or $native -eq 3) { $mono = New-Finding -Value ($native -eq 2) -Note 'Campo pubblico dmColor; nessuna informazione proprietaria esportata.' }
    if ($native -eq 4) { $mono = New-Unknown 'Nessun DEVMODE specifico; non equivale a colore o stampa privata disabilitati.' }
    [ordered]@{ DevModePresent=(New-Finding -Value $present -Note 'Solo presenza del puntatore restituito da GetPrinter.'); Monochrome=$mono }
}

try {
    if ([Environment]::OSVersion.Platform -ne 'Win32NT') { throw 'WindowsRequired' }
    $inventory = Get-QueueInventory
    if ($ListOnly) {
        [ordered]@{InventoryAvailable=$inventory.Available;PrimaryAvailable=$inventory.PrimaryAvailable;FallbackAvailable=$inventory.FallbackAvailable;RuntimeQueues=@($inventory.Queues)} | ConvertTo-Json -Depth 8 -Compress
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($QueueName)) {
        $selected = @($inventory.Queues | Where-Object { $_.Name -ieq $QueueName })
        if (-not $inventory.Available -or $selected.Count -ne 1) { throw 'SelectedQueueUnavailable' }
    }
    if (-not ('IsafomReadOnlySpoolerV1' -as [type])) { Add-Type -TypeDefinition $nativeSource -Language CSharp }
    $os = $null
    try { $os = Get-CimInstance -ClassName Win32_OperatingSystem } catch { }
    $windows = New-Unknown 'Versione Windows non leggibile.'
    $build = New-Unknown 'Build Windows non leggibile.'
    if ($null -ne $os -and $os.Version -match '^\d+\.\d+\.\d+(\.\d+)?$' -and $os.BuildNumber -match '^\d{4,6}$') {
        $windows = New-Finding -Value $os.Version
        $build = New-Finding -Value $os.BuildNumber
    }
    $architecture = New-Unknown 'Architettura non leggibile.'
    try {
        $processors = @(Get-CimInstance -ClassName Win32_Processor)
        if ($processors.Count -gt 0) { $architecture = New-Finding -Value (@($processors | Where-Object { $_.Architecture -ne 9 }).Count -eq 0) -Note 'True significa processore x64.' }
    } catch { }
    $spooler = New-Unknown 'Stato del servizio non leggibile.'
    try { $spooler = New-Finding -Value ((Get-Service -Name Spooler).Status -eq 'Running') } catch { }
    $inventoryAvailable = $inventory.Available
    $queues = @($inventory.Queues | Where-Object { $_.ModelHint })
    if (-not [string]::IsNullOrWhiteSpace($QueueName)) { $queues = $selected }
    $drivers = @()
    $allKyoceraDrivers = @()
    $driverInventoryAvailable = $false
    $ports = @()
    try {
        $drivers = @(Get-PrinterDriver)
        $allKyoceraDrivers = @($drivers | Where-Object { $_.Name -match '(?i)Kyocera|TASKalfa' } | Sort-Object Name)
        $driverInventoryAvailable = $true
    } catch { }
    try { $ports = @(Get-CimInstance -ClassName Win32_TCPIPPrinterPort) } catch { }
    $entries = @()
    $driverInventory = @()
    $driverIndex = 0
    foreach ($candidate in @($allKyoceraDrivers | Select-Object -First 32)) {
        $driverIndex++
        $candidateName = New-Unknown 'Nome driver non standard omesso per privacy.'
        if ($candidate.Name -ceq $expectedDriver) { $candidateName = New-Finding -Value $expectedDriver }
        $candidateVersion = Get-DriverFileVersion $candidate
        $driverInventory += [ordered]@{
            Alias=('D{0:D2}' -f $driverIndex); Name=$candidateName
            IsExpectedDriver=(New-Finding -Value ($candidate.Name -ieq $expectedDriver))
            Model3554ciInName=(New-Finding -Value ($candidate.Name -match '(?i)3554ci'))
            FileVersion=$candidateVersion
        }
    }
    $index = 0
    foreach ($queue in $queues) {
        $index++
        $driverVersion = New-Unknown 'Driver associato alla coda non identificato in modo univoco.'
        $associated = @($drivers | Where-Object { $_.Name -ieq $queue.DriverName })
        $driverPackage = Get-DriverPackageFindings $null
        if ($associated.Count -eq 1) { $driverVersion = Get-DriverFileVersion $associated[0] }
        if ($associated.Count -eq 1) { $driverPackage = Get-DriverPackageFindings $associated[0] }
        if ($driverVersion.State -ne 'Verificata') {
            try {
                $wmiDrivers = @(Get-CimInstance -ClassName Win32_PrinterDriver | Where-Object { $_.Name -ieq $queue.DriverName -or $_.Name.StartsWith($queue.DriverName + ',', [StringComparison]::OrdinalIgnoreCase) })
                if ($wmiDrivers.Count -eq 1) { $driverVersion = Get-DriverFileVersion $wmiDrivers[0] }
            } catch { }
        }
        $driverName = New-Unknown 'Nome driver non standard omesso; metadati riferiti al driver effettivo della coda selezionata.'
        if ($queue.DriverName -ieq $expectedDriver) { $driverName = New-Finding -Value $expectedDriver }
        if ($queue.DriverName -ceq 'Kyocera CS 3554ci KX') { $driverName = New-Finding -Value 'Kyocera CS 3554ci KX' -Note 'Nome tecnico pubblico riconosciuto; distinto dal modello TASKalfa previsto.' }
        # Custom names can contain personal data. Never export them or hashes derived from them.
        # PowerShell variable names are case-insensitive: keep this finding
        # distinct from the string parameter $QueueName used for selection.
        $queueNameFinding = New-Unknown 'Nome personalizzato omesso per privacy; identificare localmente la coda tramite Alias.'
        if ($queue.Name -ceq $expectedDriver) { $queueNameFinding = New-Finding -Value $expectedDriver }
        $port = @($ports | Where-Object { $_.Name -eq $queue.PortName })
        $portInfo = [ordered]@{
            Name=(New-Unknown 'Nome porta personalizzato o non leggibile; omesso per privacy.')
            Host=(New-Unknown 'Host personalizzato o non leggibile; omesso per privacy.')
            Protocol=(New-Unknown 'Porta non identificata come Standard TCP/IP.')
            PortNumber=(New-Unknown 'Numero porta non leggibile.')
            SnmpEnabled=(New-Unknown 'Stato SNMP non leggibile.')
            SnmpCommunityMatches=(New-Unknown 'Community non leggibile; nessuna community esportata.')
            SnmpIndex=(New-Unknown 'Indice SNMP non leggibile.')
        }
        if ($port.Count -eq 1) {
            $p = $port[0]
            if ($p.Name -ceq $expectedHost -or $p.Name -ceq $expectedIp) { $portInfo.Name = New-Finding -Value $p.Name }
            if ($p.HostAddress -ieq $expectedHost -or $p.HostAddress -ceq $expectedIp) { $portInfo.Host = New-Finding -Value $p.HostAddress.ToLowerInvariant() }
            if ($null -ne $p.Protocol -and $p.Protocol -in @(1,2)) { $portInfo.Protocol = New-Finding -Value $(if ($p.Protocol -eq 1) { 'RAW' } else { 'LPR' }) }
            if ($null -ne $p.PortNumber) { $portInfo.PortNumber = New-Finding -Value ([int]$p.PortNumber) }
            if ($null -ne $p.SNMPEnabled) { $portInfo.SnmpEnabled = New-Finding -Value ([bool]$p.SNMPEnabled) }
            if ($null -ne $p.SNMPCommunity) { $portInfo.SnmpCommunityMatches = New-Finding -Value ($p.SNMPCommunity -ceq 'isafom') -Note 'Solo confronto con il valore previsto; stringa non esportata.' }
            if ($null -ne $p.SNMPDevIndex) { $portInfo.SnmpIndex = New-Finding -Value ([int]$p.SNMPDevIndex) }
        }
        $privateNote = 'Non esiste un lettore Kyocera documentato e validato in questa versione. Valore non letto; non significa disabilitato.'
        $sharing = New-Unknown 'Stato della condivisione non leggibile.'
        if ($null -ne $queue.Shared) { $sharing = New-Finding -Value ([bool]$queue.Shared) }
        $entries += [ordered]@{
            Alias=('Q{0:D2}' -f $index); QueueName=$queueNameFinding
            DriverName=$driverName; DriverVersion=$driverVersion; DriverPackage=$driverPackage
            UsesExpectedDriver=(New-Finding -Value ($queue.DriverName -ieq $expectedDriver))
            QueueInventorySource=(New-Finding -Value $queue.Source)
            Shared=$sharing; Port=$portInfo
            Global=(Get-ColorFindings -QueueName $queue.Name -Level 8)
            CurrentUser=(Get-ColorFindings -QueueName $queue.Name -Level 9)
            Proprietary=[ordered]@{
                UserName=(New-Unknown $privateNote); JobAccounting=(New-Unknown $privateNote)
                SpecificAccount=(New-Unknown $privateNote); JobAccountingPreferences=(New-Unknown $privateNote)
                PrivatePrint=(New-Unknown $privateNote); PasswordPromptDisabled=(New-Unknown $privateNote)
                AccountAndPrivatePasswordMatch=(New-Unknown 'Non vengono letti né confrontati codici personali dalla diagnostica.')
            }
        }
    }
    $countFinding = New-Unknown 'Inventario code non disponibile. Non si deduce l''assenza della stampante.'
    if ($inventoryAvailable) { $countFinding = New-Finding -Value $queues.Count }
    $queueInventory = @()
    $inventoryIndex = 0
    foreach ($q in @($inventory.Queues | Select-Object -First 128)) {
        $inventoryIndex++
        $queueInventory += [ordered]@{Alias=('P{0:D3}' -f $inventoryIndex);ModelHint=[bool]$q.ModelHint;UsesExpectedDriver=($q.DriverName -ieq $expectedDriver);Selected=(@($queues | Where-Object {$_.Name -ieq $q.Name}).Count -eq 1);Source=$q.Source}
    }
    $report = [ordered]@{
        SchemaVersion=1; GeneratedUtc=[DateTime]::UtcNow.ToString('o')
        Scope='Solo configurazione locale e sessione corrente; nessuna stampa o verifica fisica.'
        Environment=[ordered]@{ WindowsVersion=$windows; WindowsBuild=$build; X64=$architecture; SpoolerRunning=$spooler }
        DriverInventoryAvailable=(New-Finding -Value $driverInventoryAvailable -Note 'Inventario driver Kyocera/TASKalfa; nomi non standard omessi, massimo 32 voci.')
        KyoceraDrivers=$driverInventory
        QueueDiscovery=[ordered]@{PrimaryAvailable=$inventory.PrimaryAvailable;FallbackAvailable=$inventory.FallbackAvailable;TotalCount=@($inventory.Queues).Count;Truncated=(@($inventory.Queues).Count -gt 128);Entries=$queueInventory}
        SelectionMode=$(if ([string]::IsNullOrWhiteSpace($QueueName)) {'Candidati Kyocera; nessun confronto con nome driver esatto richiesto'} else {'Scelta esplicita della coda; nome mostrato soltanto localmente'})
        QueueCount=$countFinding; Queues=$entries
        RealValidation='IN ATTESA DI VALIDAZIONE REALE'
        Privacy='Nessun Account ID, PIN, nome utente personale, hostname del PC, hive, PrintTicket o blob DEVMODE esportato. Nomi coda/porta non standard omessi.'
    }
    # Private pipe protocol for the read-only collector. Runtime queue names are
    # consumed in memory by the parent process, never included in portable reports.
    if ($InMemory) {
        [ordered]@{ Report=$report; RuntimeQueues=@($queues | ForEach-Object { [ordered]@{ Name=$_.Name; DriverName=$_.DriverName;PortName=$_.PortName;Source=$_.Source;ModelHint=$_.ModelHint } }) } | ConvertTo-Json -Depth 20 -Compress
        return
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path ([Environment]::GetFolderPath('MyDocuments')) ('ISAFOM_Diagnostica_' + [DateTime]::Now.ToString('yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N').Substring(0,6))
    }
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destination) { throw 'DestinationExists' }
    New-Item -ItemType Directory -Path $destination -ErrorAction Stop | Out-Null
    $jsonPath = Join-Path $destination 'Report.json'
    $textPath = Join-Path $destination 'Report.md'
    $report | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add('# Diagnostica Kyocera ISAFOM')
    $lines.Add('')
    $lines.Add('Stati: Verificata / Rilevata indirettamente / Non verificabile. Un dato mancante non significa disabilitato.')
    $lines.Add('')
    function Add-Readable { param($Object,[string]$Prefix)
        foreach ($key in $Object.Keys) {
            $item = $Object[$key]
            if ($item -is [System.Collections.IDictionary] -and $item.Contains('State')) {
                $value = '—'
                if ($null -ne $item.Value) { $value = [string]$item.Value }
                $lines.Add(('- **{0}{1}**: {2} — {3}. {4}' -f $Prefix,$key,$item.State,$value,$item.Note))
            }
            elseif ($item -is [System.Collections.IDictionary]) { Add-Readable $item ($Prefix + $key + ' / ') }
        }
    }
    Add-Readable $report.Environment 'Windows / '
    $lines.Add(('- **Code Kyocera**: {0} — {1}' -f $report.QueueCount.State,$report.QueueCount.Value))
    foreach ($entry in $entries) { $lines.Add(''); $lines.Add('## Coda ' + $entry.Alias); $lines.Add(''); Add-Readable $entry '' }
    $lines.Add(''); $lines.Add('IN ATTESA DI VALIDAZIONE REALE: pannello, Job Accounting, rilascio privato, riavvio e nuova sessione.')
    $lines.Add(''); $lines.Add('Assistenza informatica: Andrea Esposito — CNR-ISAFOM Portici')
    $lines | Set-Content -LiteralPath $textPath -Encoding UTF8
    Compress-Archive -LiteralPath @($jsonPath,$textPath) -DestinationPath (Join-Path $destination 'Diagnostica_Kyocera_ISAFOM.zip') -CompressionLevel Optimal
    Write-Output ('Diagnostica completata. Report locale: ' + $textPath)
    Write-Output 'Condividere soltanto il file Diagnostica_Kyocera_ISAFOM.zip. Le preferenze proprietarie restano non verificabili.'
}
catch {
    # No raw CIM/spooler errors: they may contain local names or paths.
    Write-Error 'Diagnostica non completata. Verificare Windows, disponibilità dei comandi e una cartella di destinazione nuova e scrivibile. Rivolgersi ad Andrea Esposito.' -ErrorAction Continue
    exit 1
}
