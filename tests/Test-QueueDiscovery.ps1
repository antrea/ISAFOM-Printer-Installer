#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$diagnostic = Join-Path $taskRoot 'diagnostics\Diagnostica_Kyocera_ISAFOM.ps1'
$script:passed = 0
function Assert-Test([bool]$Condition,[string]$Name) {
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:passed++; Write-Output ('PASS ' + $Name)
}
$sentinel = '0' + (Get-Random -Minimum 0 -Maximum 10000000).ToString('D7',[Globalization.CultureInfo]::InvariantCulture)
$personal = 'TEST_' + [Guid]::NewGuid().ToString('N').Substring(0,20)
$syntheticName = 'Coda ' + $personal + ' ' + $sentinel
$fallbackName = 'Coda solo WMI ' + $sentinel
$localDll = @(Get-PrinterDriver | Where-Object Name -eq 'Microsoft Print To PDF')[0].Path
$expectedFileVersion = (Get-Item -LiteralPath $localDll).VersionInfo.FileVersionRaw.ToString()
function PrinterFingerprint {
    $text = @(Get-CimInstance Win32_Printer | Sort-Object Name | Select-Object Name,DriverName,PortName,Shared,Default) | ConvertTo-Json -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
}
$before = PrinterFingerprint
try {
    $fixtureState = [pscustomobject]@{PrimaryFails=$false;FallbackFails=$false;DriverFallback=$false}
    # In-memory fixtures only. No synthetic queue or registry key is created.
    function Get-Printer {
        if ($fixtureState.PrimaryFails) { throw 'SyntheticPrimaryFailure' }
        [pscustomobject]@{Name=$syntheticName;DriverName='KX Universal 3554ci';PortName=('Porta ' + $sentinel);Shared=$null}
        [pscustomobject]@{Name='Altra coda';DriverName='Altro driver';PortName='Altro';Shared=$false}
    }
    function Get-PrinterDriver {
        if ($fixtureState.DriverFallback) { throw 'SyntheticDriverFailure' }
        [pscustomobject]@{Name='KX Universal 3554ci';Path=$localDll;DriverVersion=[uint64]2251821446135808;ConfigFile=$localDll}
        [pscustomobject]@{Name='Kyocera TASKalfa 3554ci KX';Path=$localDll}
    }
    function Get-CimInstance { param([string]$ClassName)
        switch ($ClassName) {
            'Win32_Printer' {
                if ($fixtureState.FallbackFails) { throw 'SyntheticFallbackFailure' }
                [pscustomobject]@{Name=$syntheticName.ToLowerInvariant();DriverName='KX Universal 3554ci';PortName=('Porta ' + $sentinel);Shared=$null}
                [pscustomobject]@{Name=$fallbackName;DriverName='Altro driver';PortName='IP diversa';Shared=$false}
            }
            'Win32_OperatingSystem' { [pscustomobject]@{Version='10.0.26100';BuildNumber='26100'} }
            'Win32_Processor' { [pscustomobject]@{Architecture=9} }
            'Win32_PrinterDriver' {
                [pscustomobject]@{Name='KX Universal 3554ci,3,Windows x64';DriverPath=$localDll}
                [pscustomobject]@{Name='Altro driver,3,Windows x64';DriverPath=$localDll}
            }
            'Win32_TCPIPPrinterPort' { @() }
            default { throw 'UnexpectedSyntheticQuery' }
        }
    }
    $inventory = (& $diagnostic -InMemory -ListOnly) | ConvertFrom-Json
    Assert-Test ($inventory.InventoryAvailable -and $inventory.RuntimeQueues.Count -eq 3) 'Discovery includes all queues, no exact-driver filter'
    Assert-Test (@($inventory.RuntimeQueues | Where-Object Name -ieq $syntheticName).Count -eq 1) 'Primary/CIM queue names merged case-insensitively'
    Assert-Test (($inventory.RuntimeQueues | Where-Object Name -ieq $syntheticName).Source -ceq 'Get-Printer + Win32_Printer') 'Merged inventory retains both read sources'
    Assert-Test (@($inventory.RuntimeQueues | Where-Object Name -ieq $fallbackName).Count -eq 1) 'CIM-only queue included'
    $capture = (& $diagnostic -InMemory -QueueName $syntheticName) | ConvertFrom-Json
    $report = $capture.Report
    Assert-Test ($report.QueueCount.Value -eq 1 -and $report.Queues.Count -eq 1) 'Selected variant driver queue acquired exactly once'
    Assert-Test ($report.Queues[0].QueueName -is [pscustomobject] -and $report.Queues[0].QueueName.State -ceq 'Non verificabile' -and $null -eq $report.Queues[0].QueueName.Value) 'QueueName remains a structured finding with the personal name withheld'
    Assert-Test ($capture.RuntimeQueues[0].Name -ceq $syntheticName) 'QueueName finding does not overwrite the selected runtime name'
    Assert-Test ($report.Queues[0].DriverVersion.State -ceq 'Verificata' -and $report.Queues[0].DriverVersion.Value -ceq $expectedFileVersion) 'MSFT Path property reads actual local DLL version'
    Assert-Test ($report.Queues[0].DriverPackage.RegisteredSpoolerVersion.Value -ceq '8.5.2405.0') 'Registered package version decoded independently from the DLL fixed version'
    Assert-Test ($report.Queues[0].DriverPackage.InfDriverVersion.State -ceq 'Non verificabile' -and $null -eq $report.Queues[0].DriverPackage.InfDriverVersion.Value) 'Missing INF version remains unknown, not inferred from the DLL'
    Assert-Test ($report.Queues[0].DriverPackage.ConfigDllFileVersionText.Value -ceq (Get-Item -LiteralPath $localDll).VersionInfo.FileVersion) 'Textual configuration DLL version kept separate from package and fixed version'
    Assert-Test (-not $report.Queues[0].UsesExpectedDriver.Value -and $null -eq $report.Queues[0].DriverName.Value) 'Variant driver not falsely named as expected driver'
    Assert-Test ($null -eq $report.Queues[0].Shared.Value -and $report.Queues[0].Shared.State -ceq 'Non verificabile') 'Missing sharing value remains unknown after merge'
    Assert-Test ($report.QueueDiscovery.TotalCount -eq 3 -and @($report.QueueDiscovery.Entries | Where-Object Selected).Count -eq 1) 'Portable discovery counts and selection are explicit'
    $safeJson = $report | ConvertTo-Json -Depth 24
    Assert-Test (-not $safeJson.Contains($sentinel) -and -not $safeJson.Contains($personal) -and -not $safeJson.Contains($localDll)) 'Portable report omits personal queue names, port names and DLL paths'
    $fixtureState.PrimaryFails = $true
    $inventory = (& $diagnostic -InMemory -ListOnly) | ConvertFrom-Json
    Assert-Test (-not $inventory.PrimaryAvailable -and $inventory.FallbackAvailable -and $inventory.RuntimeQueues.Count -eq 2) 'CIM fallback works when Get-Printer fails'
    $capture = (& $diagnostic -InMemory -QueueName $fallbackName) | ConvertFrom-Json
    Assert-Test ($capture.Report.QueueCount.Value -eq 1 -and $capture.RuntimeQueues[0].Name -ceq $fallbackName) 'Explicit non-Kyocera queue works through fallback source'
    $fixtureState.DriverFallback = $true
    $capture = (& $diagnostic -InMemory -QueueName $syntheticName) | ConvertFrom-Json
    Assert-Test ($capture.Report.Queues[0].DriverVersion.State -ceq 'Verificata' -and $capture.Report.Queues[0].DriverVersion.Value -ceq $expectedFileVersion) 'Win32_PrinterDriver DriverPath fallback reads actual DLL version'
    $fixtureState.FallbackFails = $true
    $inventory = (& $diagnostic -InMemory -ListOnly) | ConvertFrom-Json
    Assert-Test (-not $inventory.InventoryAvailable -and $inventory.RuntimeQueues.Count -eq 0) 'Both read failures are not reported as a verified empty inventory'
    Remove-Item -LiteralPath Function:\Get-Printer,Function:\Get-PrinterDriver,Function:\Get-CimInstance
    Assert-Test ((PrinterFingerprint) -ceq $before) 'Synthetic discovery regression tests leave all real printers unchanged'
    Write-Output ('RESULT ' + $script:passed + ' queue discovery/version regression checks passed. No printer changes.')
    exit 0
}
catch {
    if ($_.Exception.Message -like 'FAIL *') { Write-Output $_.Exception.Message }
    else { Write-Output ('FAIL discovery test: ' + $_.Exception.GetType().Name) }
    exit 1
}
