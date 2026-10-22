#requires -Version 5.1
<# Compares only a fixed allowlist. No secret fields, arbitrary notes or property names are copied into output. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Reference,
    [Parameter(Mandatory=$true)][string]$Candidate,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-SafeReport { param([string]$Path)
    $info = Get-Item -LiteralPath $Path
    if ($info.Length -gt 1048576) { throw 'ReportTooLarge' }
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($report.SchemaVersion -ne 1 -or $null -eq $report.Queues -or @($report.Queues).Count -gt 64) { throw 'Schema' }
    $report
}
function Get-SafeFinding { param($Root,[string]$Path,[string]$Type)
    $unknown = [ordered]@{ State='Non verificabile'; Value=$null }
    $current = $Root
    foreach ($part in $Path.Split('.')) {
        if ($null -eq $current -or $null -eq $current.PSObject.Properties[$part]) { return $unknown }
        $current = $current.PSObject.Properties[$part].Value
    }
    if ($null -eq $current -or $null -eq $current.PSObject.Properties['State'] -or $null -eq $current.PSObject.Properties['Value']) { return $unknown }
    if ($current.State -notin @('Verificata','Rilevata indirettamente','Non verificabile')) { return $unknown }
    if ($current.State -eq 'Non verificabile' -or $null -eq $current.Value) { return $unknown }
    $value = $current.Value
    $valid = $false
    switch ($Type) {
        'Boolean' { $valid = $value -is [bool] }
        'Count' { $valid = ($value -is [int] -or $value -is [long]) -and $value -ge 0 -and $value -le 64 }
        'Integer' { $valid = ($value -is [int] -or $value -is [long]) -and $value -ge 0 -and $value -le 65535 }
        'Version' { $valid = $value -is [string] -and $value -match '^\d{1,6}(\.\d{1,6}){1,3}$' }
        'Protocol' { $valid = $value -cin @('RAW','LPR') }
        'Host' { $valid = $value -cin @('stampante.isafom.cnr.it','140.164.75.11') }
    }
    if (-not $valid) { return $unknown }
    [ordered]@{ State=$current.State; Value=$value }
}

try {
    $referenceReport = Read-SafeReport $Reference
    $candidateReport = Read-SafeReport $Candidate
    $fields = [ordered]@{
        'DriverVersion'='Version'; 'Shared'='Boolean'; 'Port.Host'='Host'; 'Port.Protocol'='Protocol'
        'Port.PortNumber'='Integer'; 'Port.SnmpEnabled'='Boolean'; 'Port.SnmpCommunityMatches'='Boolean'; 'Port.SnmpIndex'='Integer'
        'Global.DevModePresent'='Boolean'; 'Global.Monochrome'='Boolean'
        'CurrentUser.DevModePresent'='Boolean'; 'CurrentUser.Monochrome'='Boolean'
        'Proprietary.JobAccounting'='Boolean'; 'Proprietary.SpecificAccount'='Boolean'
        'Proprietary.JobAccountingPreferences'='Boolean'; 'Proprietary.PrivatePrint'='Boolean'
        'Proprietary.PasswordPromptDisabled'='Boolean'; 'Proprietary.AccountAndPrivatePasswordMatch'='Boolean'
    }
    $rows = New-Object 'System.Collections.Generic.List[object]'
    function Add-Comparison { param([string]$Field,$Left,$Right)
        $state = 'Non confrontabile'
        if ($Left.State -eq 'Verificata' -and $Right.State -eq 'Verificata') {
            $state = 'Diversa'
            if ($Left.Value -ceq $Right.Value) { $state = 'Uguale' }
        }
        $rows.Add([ordered]@{ Field=$Field; Reference=$Left; Candidate=$Right; Comparison=$state })
    }
    Add-Comparison 'QueueCount' (Get-SafeFinding $referenceReport 'QueueCount' 'Count') (Get-SafeFinding $candidateReport 'QueueCount' 'Count')
    # Multiple queues cannot be paired safely from privacy-redacted identifiers. Never guess the pairing.
    if (@($referenceReport.Queues).Count -eq 1 -and @($candidateReport.Queues).Count -eq 1) {
        foreach ($field in $fields.Keys) {
            Add-Comparison $field (Get-SafeFinding $referenceReport.Queues[0] $field $fields[$field]) (Get-SafeFinding $candidateReport.Queues[0] $field $fields[$field])
        }
    }
    $differences = @($rows | Where-Object { $_.Comparison -eq 'Diversa' }).Count
    $notComparable = @($rows | Where-Object { $_.Comparison -eq 'Non confrontabile' }).Count
    $completePair = (@($referenceReport.Queues).Count -eq 1 -and @($candidateReport.Queues).Count -eq 1)
    $status = 'INCOMPLETO'
    if ($differences -gt 0) { $status = 'DIFFERENZE RILEVATE' }
    # Even identical public settings do not certify proprietary behavior or real output.
    $result = [ordered]@{ SchemaVersion=1; Status=$status; Compared=$rows.Count; Differences=$differences;
        NotComparable=$notComparable; UniqueQueuePair=$completePair; Fields=@($rows.ToArray());
        RealValidation='IN ATTESA DI VALIDAZIONE REALE';
        Note='Uguaglianza delle opzioni pubbliche non certifica Nome, Job Accounting, codici, stampa privata o persistenza.' }
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $destination) { throw 'DestinationExists' }
    New-Item -ItemType Directory -Path $destination | Out-Null
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'Confronto.json') -Encoding UTF8
    $text = New-Object 'System.Collections.Generic.List[string]'
    $text.Add('# Confronto diagnostica ISAFOM'); $text.Add(''); $text.Add('Stato: **' + $status + '**'); $text.Add('')
    foreach ($row in $rows) { $text.Add(('- {0}: {1}' -f $row.Field,$row.Comparison)) }
    $text.Add(''); $text.Add('I valori non verificabili restano non confrontabili, anche se mancanti su entrambi i PC.')
    $text.Add('Se sono presenti più code, Andrea Esposito deve identificarle localmente; il confronto non le abbina automaticamente.')
    $text.Add('IN ATTESA DI VALIDAZIONE REALE: il confronto non sostituisce la prova sul dispositivo.')
    $text | Set-Content -LiteralPath (Join-Path $destination 'Confronto.md') -Encoding UTF8
    Write-Output ('Confronto salvato. Stato: ' + $status + '. Nessuna impostazione è stata modificata.')
    if ($differences -gt 0) { exit 1 }
    exit 2
}
catch {
    Write-Error 'Confronto non completato: usare due Report.json schema 1 e una nuova cartella di destinazione.' -ErrorAction Continue
    exit 3
}
