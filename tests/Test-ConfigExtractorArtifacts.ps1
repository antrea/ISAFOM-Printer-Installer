#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$workRoot=Split-Path -Parent $projectRoot
$directory=[IO.Path]::GetFullPath($ReportDirectory)
if(-not $directory.StartsWith($workRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'ReportOutsideWorkspace'}
$manifest=Get-Content -LiteralPath (Join-Path $directory 'Manifest.json') -Raw|ConvertFrom-Json
$expected=@('Configurazione.json','Evidenze_Diagnostiche.json','Piano_Installer.json','Rapporto.md')
if((($manifest.Files.Name|Sort-Object) -join '|') -cne (($expected|Sort-Object) -join '|')){throw 'UnexpectedManifestFiles'}
foreach($file in $manifest.Files){if((Get-FileHash -LiteralPath (Join-Path $directory $file.Name)).Hash -cne $file.SHA256){throw 'ReportFileHashMismatch'}}
$zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $directory 'Configurazione_Kyocera.zip'))
try {
    $names=@($expected)+@('Manifest.json')
    if(@($zip.Entries).Count -ne 5 -or @($zip.Entries.FullName|Sort-Object -Unique).Count -ne 5){throw 'UnexpectedArchiveCount'}
    foreach($entry in $zip.Entries){
        if($entry.FullName -cnotin $names -or $entry.Length -gt 2MB){throw 'UnexpectedArchiveEntry'}
        $stream=$entry.Open();try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -cne (Get-FileHash -LiteralPath (Join-Path $directory $entry.FullName)).Hash){throw 'ArchiveAndReportDiffer'}
    }
}finally{$zip.Dispose()}
$report=Get-Content -LiteralPath (Join-Path $directory 'Configurazione.json') -Raw|ConvertFrom-Json
$plan=Get-Content -LiteralPath (Join-Path $directory 'Piano_Installer.json') -Raw|ConvertFrom-Json
if($report.CompleteForAutomaticInstallation -or $plan.Executable -or $plan.AutomaticInstallationReady){throw 'FalseInstallerReadiness'}
if(-not $plan.IdentityRules.PreserveLeadingZeros -or -not $plan.IdentityRules.UseExactSameCodeForAccountingAndPrivatePassword -or $plan.IdentityRules.CopyReferenceUsersIdentity){throw 'UnsafeIdentityPlan'}
if($report.Settings.Count -lt 60 -or $report.PersonalReadback.Count -ne 2 -or $report.ParameterRules.Count -lt 6){throw 'ExtractionCoverageUnexpected'}
if($report.Integrity.SelectedReadableConfigurationUnchanged -ne $true -or $report.Integrity.QueueInventoryUnchanged -ne $true -or $report.Integrity.DefaultPrinterUnchanged -ne $true){throw 'IntegrityNotVerifiedOnThisPc'}
foreach($file in $expected){
    $text=Get-Content -LiteralPath (Join-Path $directory $file) -Raw
    if($text -match '<(?:\w+:)?(?:PrintTicket|PrintCapabilities)|PageDevmodeSnapshot|(?i)[A-Z]:[\\/]Users[\\/]'){throw 'RawOrPersonalExportDetected'}
}
foreach($name in @('PublicDevMode.cs','SentinelSnapshot.cs','StandardSettings.cs','PersonalReadback.cs','DirectConversionProbe.cs','IdentityVerification.cs','IdentityVerifierWindow.cs','Program.cs')){
    $source=Get-Content -LiteralPath (Join-Path $projectRoot ('src\Isafom.ConfigExtractor\'+$name)) -Raw
    if($source -match '\b(?:SetPrinter|SetPrinterData|SetValue|DeleteValue|DocumentProperties|StartDocPrinter|WritePrinter|AddPrinter|DeletePrinter|Commit)\s*\('){throw 'UnexpectedConfigurationWriter'}
}
Write-Output ('PASS ConfigExtractorArtifacts;ArchiveEntries=5;Properties='+$report.Settings.Count+';KxRules='+$report.ParameterRules.Count+';IntegrityVerified=True;NoConfigurationWriter=True')
