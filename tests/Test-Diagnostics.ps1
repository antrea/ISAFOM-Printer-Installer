#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root ('artifacts\test-diagnostics-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts | Out-Null
$script:passed = 0
function Assert-Test { param([bool]$Condition,[string]$Name)
    if (-not $Condition) { throw ('FAIL ' + $Name) }
    $script:passed++
    Write-Output ('PASS ' + $Name)
}
function Get-PrinterFingerprint {
    $items = @(Get-CimInstance -ClassName Win32_Printer | Sort-Object Name | Select-Object Name,DriverName,PortName,Shared,Default)
    $json = ConvertTo-Json -InputObject $items -Compress
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
}
try {
    $scripts = @(Get-ChildItem -LiteralPath (Join-Path $root 'diagnostics') -Filter '*.ps1') +
        @(Get-ChildItem -LiteralPath (Join-Path $root 'build') -Filter '*.ps1') +
        @(Get-ChildItem -LiteralPath (Join-Path $root 'src\Isafom.Windows\Scripts') -Filter '*.ps1')
    foreach ($file in $scripts) {
        $errors = $null; $tokens = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
        Assert-Test (@($errors).Count -eq 0) ('Parse ' + $file.Name)
        if ($file.Directory.Name -eq 'diagnostics') {
            $commands = @($ast.FindAll({param($a) $a -is [Management.Automation.Language.CommandAst]},$true) | ForEach-Object { $_.GetCommandName() })
            $banned = @('Set-ItemProperty','New-ItemProperty','Remove-ItemProperty','Remove-Item','Set-Printer','Add-Printer','Remove-Printer','Add-PrinterDriver','Remove-PrinterDriver','Add-PrinterPort','Set-PrintConfiguration','Restart-Service','Start-Service','Stop-Service','Start-Process','Invoke-Expression','Invoke-WebRequest','Invoke-RestMethod','Out-Printer','reg','reg.exe','pnputil','pnputil.exe','rundll32','rundll32.exe')
            Assert-Test (@($commands | Where-Object { $_ -in $banned }).Count -eq 0) ('No configuration or printing commands in ' + $file.Name)
        }
    }
    $source = Get-Content -LiteralPath (Join-Path $root 'diagnostics\Diagnostica_Kyocera_ISAFOM.ps1') -Raw
    Assert-Test ($source -notmatch 'extern\s+\w+\s+(SetPrinter|StartDocPrinter|WritePrinter|DocumentProperties)\b') 'Diagnostic native API has no setter or print call'
    $before = Get-PrinterFingerprint
    $live = Join-Path $artifacts 'live'
    & (Join-Path $root 'diagnostics\Diagnostica_Kyocera_ISAFOM.ps1') -OutputDirectory $live
    Assert-Test ((Get-PrinterFingerprint) -ceq $before) 'Printer queues, drivers, ports, sharing and default unchanged after diagnostics'
    $liveJson = Get-Content -LiteralPath (Join-Path $live 'Report.json') -Raw
    $report = $liveJson | ConvertFrom-Json
    Assert-Test ($report.SchemaVersion -eq 1) 'Live diagnostic schema'
    Assert-Test ($report.RealValidation -ceq 'IN ATTESA DI VALIDAZIONE REALE') 'Live diagnostic never claims physical validation'
    Assert-Test ($liveJson -notmatch 'PageDevmodeSnapshot|dmDriverExtra|HKEY_|LoginPassword') 'No private binary/configuration export in live report'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $live 'Diagnostica_Kyocera_ISAFOM.zip'))
    try { Assert-Test (($archive.Entries.FullName | Sort-Object) -join '|' -ceq 'Report.json|Report.md') 'Shareable archive contains only the two allowlisted reports' }
    finally { $archive.Dispose() }
    # Read-only synthetic inventory: a missing Shared property must never become false.
    try {
        function Get-Printer { [pscustomobject]@{Name='Kyocera TASKalfa 3554ci KX';DriverName='Kyocera TASKalfa 3554ci KX';PortName='synthetic-unavailable';Shared=$null} }
        function Get-PrinterDriver { @() }
        function Get-CimInstance { param([string]$ClassName)
            # Isolate BOTH queue sources. A real queue with the same name must
            # not fill in this fixture's deliberately unavailable Shared value.
            if ($ClassName -eq 'Win32_Printer') {
                [pscustomobject]@{Name='Kyocera TASKalfa 3554ci KX';DriverName='Kyocera TASKalfa 3554ci KX';PortName='synthetic-unavailable';Shared=$null}
            }
            else { CimCmdlets\Get-CimInstance -ClassName $ClassName }
        }
        $missing = Join-Path $artifacts 'synthetic-missing-sharing'
        & (Join-Path $root 'diagnostics\Diagnostica_Kyocera_ISAFOM.ps1') -OutputDirectory $missing
        $missingReport = Get-Content -LiteralPath (Join-Path $missing 'Report.json') -Raw | ConvertFrom-Json
        Assert-Test ($missingReport.Queues[0].Shared.State -ceq 'Non verificabile' -and $null -eq $missingReport.Queues[0].Shared.Value) 'Missing sharing state is not reported as disabled'
    }
    finally { Remove-Item -LiteralPath Function:\Get-Printer,Function:\Get-PrinterDriver,Function:\Get-CimInstance -ErrorAction SilentlyContinue }
    # Exercise the diagnostic's native public-header reader on an existing Microsoft queue, read-only.
    $pdf = @(Get-Printer | Where-Object { $_.DriverName -eq 'Microsoft Print To PDF' })
    if ($pdf.Count -eq 1) {
        $global = [IsafomReadOnlySpoolerV1]::Read($pdf[0].Name,8)
        $user = [IsafomReadOnlySpoolerV1]::Read($pdf[0].Name,9)
        Assert-Test ($global -in @(1,2,3,4) -and $user -in @(1,2,3,4)) 'Native DEVMODE read on existing Microsoft PDF queue (not a Kyocera test)'
        Assert-Test ((Get-PrinterFingerprint) -ceq $before) 'Native diagnostic reads preserve printer configuration'
    }
    # Synthetic fixtures deliberately include a fake secret in untrusted notes and fields.
    $sentinel = '0' + (Get-Random -Minimum 0 -Maximum 10000000).ToString('D7',[Globalization.CultureInfo]::InvariantCulture)
    function Finding($value,$state='Verificata') { @{ State=$state; Value=$value; Note=$sentinel } }
    $q = @{
        QueueName=(Finding $sentinel); DriverVersion=(Finding '8.5.2405.0'); Shared=(Finding $false)
        Port=@{Host=(Finding 'stampante.isafom.cnr.it');Protocol=(Finding 'RAW');PortNumber=(Finding 9100);SnmpEnabled=(Finding $true);SnmpCommunityMatches=(Finding $true);SnmpIndex=(Finding 1)}
        Global=@{DevModePresent=(Finding $true);Monochrome=(Finding $true)}
        CurrentUser=@{DevModePresent=(Finding $true);Monochrome=(Finding $true)}
        Proprietary=@{JobAccounting=(Finding $null 'Non verificabile');SpecificAccount=(Finding $sentinel);PrivatePrint=(Finding $false)}
        UnexpectedSecret=$sentinel
    }
    $fixture = @{SchemaVersion=1;QueueCount=(Finding 1);Queues=@($q);UnexpectedSecret=$sentinel}
    $left = Join-Path $artifacts 'reference.json'
    $right = Join-Path $artifacts 'candidate.json'
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $left -Encoding utf8
    $q.Proprietary.PrivatePrint = Finding $null 'Non verificabile'
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $right -Encoding utf8
    $compare = Join-Path $artifacts 'comparison'
    & (Get-Command pwsh.exe).Source -NoProfile -File (Join-Path $root 'diagnostics\Confronta_Diagnostica_ISAFOM.ps1') -Reference $left -Candidate $right -OutputDirectory $compare
    Assert-Test ($LASTEXITCODE -eq 2) 'Comparison remains incomplete without proprietary validation'
    $comparisonText = Get-Content -LiteralPath (Join-Path $compare 'Confronto.json') -Raw
    Assert-Test (-not $comparisonText.Contains($sentinel)) 'Untrusted values, secret-like fields and notes never copied to comparison'
    $comparison = $comparisonText | ConvertFrom-Json
    $account = $comparison.Fields | Where-Object Field -eq 'Proprietary.SpecificAccount'
    Assert-Test ($account.Comparison -ceq 'Non confrontabile' -and $null -eq $account.Reference.Value) 'Numeric account value never treated as a diagnostic boolean'
    $private = $comparison.Fields | Where-Object Field -eq 'Proprietary.PrivatePrint'
    Assert-Test ($private.Comparison -ceq 'Non confrontabile') 'Missing proprietary value is not false or equal'
    $q.Port.PortNumber = Finding 515
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $right -Encoding utf8
    $diff = Join-Path $artifacts 'different'
    & (Get-Command pwsh.exe).Source -NoProfile -File (Join-Path $root 'diagnostics\Confronta_Diagnostica_ISAFOM.ps1') -Reference $left -Candidate $right -OutputDirectory $diff
    Assert-Test ($LASTEXITCODE -eq 1) 'Changed port detected by comparison'
    $change = Get-Content -LiteralPath (Join-Path $diff 'Confronto.json') -Raw | ConvertFrom-Json
    Assert-Test (($change.Fields | Where-Object Field -eq 'Port.PortNumber').Comparison -ceq 'Diversa') 'RAW port difference is explicit'
    $fixture.Queues = @($q,$q)
    $fixture | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $right -Encoding utf8
    $multi = Join-Path $artifacts 'multiple'
    & (Get-Command pwsh.exe).Source -NoProfile -File (Join-Path $root 'diagnostics\Confronta_Diagnostica_ISAFOM.ps1') -Reference $left -Candidate $right -OutputDirectory $multi
    $multiple = Get-Content -LiteralPath (Join-Path $multi 'Confronto.json') -Raw | ConvertFrom-Json
    Assert-Test (-not $multiple.UniqueQueuePair -and $multiple.Fields.Count -eq 1) 'Multiple redacted queues are never paired by guesswork'
    Assert-Test ((Get-PrinterFingerprint) -ceq $before) 'Complete diagnostic test run preserves printer configuration'
    Write-Output ('RESULT ' + $script:passed + ' diagnostic/privacy checks passed. No print job submitted.')
    Write-Output ('Artifacts: ' + $artifacts)
    exit 0
}
catch {
    Write-Output ('FAIL diagnostic test: ' + $_.Exception.GetType().Name)
    # This test runner processes synthetic fixtures only; report the failing assertion, never live raw errors.
    if ($_.Exception.Message -like 'FAIL *') { Write-Output $_.Exception.Message }
    exit 1
}
