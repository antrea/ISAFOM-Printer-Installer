#requires -Version 7.0
[CmdletBinding()]
param([string]$Executable = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'dist\installer-0.2.7-rc1\Installa_Stampante_ISAFOM.exe' }
$output = Join-Path $root ('artifacts\published-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$script:passed = 0
function Check { param([bool]$Condition,[string]$Description)
    if (-not $Condition) { throw ('FAIL ' + $Description) }
    $script:passed++; Write-Output ('PASS ' + $Description)
}
function Run-App { param([string[]]$Arguments,[string]$InputText = '')
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    if ($Arguments -contains '--validate-config-stdin') {
        $start.RedirectStandardInput = $true
        $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($start.RedirectStandardInput) { $process.StandardInput.Write($InputText); $process.StandardInput.Close() }
    if (-not $process.WaitForExit(60000)) { throw 'Application timeout; inspect process without forcing printer operations.' }
    $process.ExitCode
}
try {
    Check ((Test-Path -LiteralPath $Executable) -and (Get-Item -LiteralPath $Executable).Length -gt 1000000) 'Self-contained EXE present'
    Check (@(Get-ChildItem -LiteralPath (Split-Path -Parent $Executable) -File).Count -eq 1) 'Application output contains one file only'
    $before = @(Get-CimInstance Win32_Printer | Sort-Object Name | Select-Object Name,DriverName,PortName,Shared,Default) | ConvertTo-Json -Compress
    $beforePorts=@(Get-PrinterPort|Sort-Object Name|Select-Object Name,PortMonitor,PrinterHostAddress,PortNumber,Protocol,SNMPEnabled,SNMPDevIndex)|ConvertTo-Json -Compress
    $png = Join-Path $output 'ui-preview.png'
    Check ((Run-App @('--render-preview',$png)) -eq 0 -and (Get-Item -LiteralPath $png).Length -gt 10000) 'Published WPF EXE renders its own UI without opening an interactive window'
    $probe = Join-Path $output 'probe.json'
    Check ((Run-App @('--probe-output',$probe)) -eq 0) 'Published EXE executes read-only embedded backend'
    $report = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Check ($report.ProprietaryStatus -like 'Non verificabile*' -and $null -eq $report.PSObject.Properties['AccountID']) 'Published probe omits secret fields and cannot certify Kyocera preferences'
    $removal=Join-Path $output 'removal-preview.json'
    Check ((Run-App @('--removal-preview-output',$removal)) -eq 0) 'Published EXE computes removal preview without UAC or mutations'
    $removalPlan=Get-Content -LiteralPath $removal -Raw|ConvertFrom-Json
    Check ($removalPlan.Fingerprint -match '^[A-F0-9]{64}$' -and $null -eq $removalPlan.PSObject.Properties['AccountID']) 'Removal preview has a bounded confirmation fingerprint and no personal codes'
    $forcePreview=Join-Path $output 'force-removal-preview.json'
    Check ((Run-App @('--force-removal-preview-output',$forcePreview)) -eq 0) 'Published EXE computes expanded force preview without elevation or mutations'
    $forcePlan=Get-Content -LiteralPath $forcePreview -Raw|ConvertFrom-Json
    Check ($forcePlan.ForceRemoval -and $forcePlan.Fingerprint -match '^[A-F0-9]{64}$' -and $null -eq $forcePlan.PSObject.Properties['AccountID']) 'Force preview binds its expanded scope and omits personal codes'
    $settings = Get-Content -LiteralPath (Join-Path $root 'config\printer-settings.json') -Raw
    Check ((Run-App @('--validate-config-stdin') $settings) -eq 0) 'Administrative settings validated in memory through stdin'
    Check ((Run-App @('--validate-config',(Join-Path $root 'config\printer-settings.json'))) -eq 0) 'Configuration validation file mode is read-only'
    $sentinel = '00992871'
    $invalid = $settings.Replace('{','{"AccountID":"' + $sentinel + '",')
    Check ((Run-App @('--validate-config-stdin') $invalid) -ne 0) 'Personal code field rejected from administrative configuration'
    $log = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CNR-ISAFOM\installer.log'
    Check (-not (Get-Content -LiteralPath $log -Raw).Contains($sentinel)) 'Configuration error does not disclose synthetic code in actual application log'
    $after = @(Get-CimInstance Win32_Printer | Sort-Object Name | Select-Object Name,DriverName,PortName,Shared,Default) | ConvertTo-Json -Compress
    Check ($before -ceq $after) 'Published application smoke tests preserve all printers and Windows default'
    $afterPorts=@(Get-PrinterPort|Sort-Object Name|Select-Object Name,PortMonitor,PrinterHostAddress,PortNumber,Protocol,SNMPEnabled,SNMPDevIndex)|ConvertTo-Json -Compress
    Check ($beforePorts -ceq $afterPorts) 'Ordinary and force previews preserve every Windows printer port'
    $expected = (Get-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') -Raw).Split(' ')[0]
    Check ((Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash -ceq $expected) 'Published executable matches SHA256 manifest'
    Write-Output ('RESULT ' + $script:passed + ' published EXE checks passed. No print job submitted.')
    Write-Output ('Artifacts: ' + $output)
    exit 0
}
catch {
    Write-Output ('FAIL published app: ' + $_.Exception.GetType().Name)
    if ($_.Exception.Message -like 'FAIL *') { Write-Output $_.Exception.Message }
    exit 1
}
