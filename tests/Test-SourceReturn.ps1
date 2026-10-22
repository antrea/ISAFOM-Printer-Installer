#requires -Version 7.0
<# Verifies the actual archive contents, hashes, allowlist and required source
   coverage against the working files. It never extracts or executes a payload. #>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArchivePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workRoot = Split-Path -Parent $projectRoot
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    $manifestEntry = $zip.GetEntry('Sorgenti_Rientro_Manifest.json')
    if ($null -eq $manifestEntry -or $manifestEntry.Length -gt 64KB) { throw 'MissingOrOversizedManifest' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.SchemaVersion -ne 1 -or -not $manifest.ManualPrivacyReviewCompleted) { throw 'UnexpectedManifest' }
    $names = @($zip.Entries.FullName)
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'DuplicateArchiveEntries' }
    if ($names.Count -ne @($manifest.Files).Count+1) { throw 'UnmanifestedArchiveEntry' }
    $history = Get-Content -LiteralPath (Join-Path $workRoot 'raccolta-locale/Modifiche_Sorgenti.json') -Raw | ConvertFrom-Json
    $required = @($history.ChangedOriginalFiles.Path) + @($history.NewSourceFiles | ForEach-Object {'ISAFOM-Printer-Installer/'+$_})
    if (@($required | Where-Object { $_ -cnotin $names }).Count) { throw 'HistoricalSourceContentsMissing' }
    $verified = 0
    foreach ($file in $manifest.Files) {
        $name = [string]$file.Path
        if ($name -match '(^|/)\.\.(/|$)|\\|:|(^|/)(bin|obj|dist|resources|\.lavoro)(/|$)' -or
            [IO.Path]::GetExtension($name) -notin @('.ps1','.cs','.csproj','.xaml','.md','.json','.cmd')) { throw 'ForbiddenArchivePath' }
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry -or $entry.Length -ne $file.Bytes -or $entry.Length -gt 1MB) { throw 'ArchiveLengthMismatch' }
        $inputStream = $entry.Open()
        try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)) } finally { $inputStream.Dispose() }
        if ($actual -cne $file.SHA256) { throw 'ArchiveHashMismatch' }
        $local = if ($name -ceq 'raccolta-locale/Modifiche_Sorgenti_Indagine_Storico.json') {
            Join-Path $workRoot 'raccolta-locale/Modifiche_Sorgenti.json'
        } else {
            if (-not $name.StartsWith('ISAFOM-Printer-Installer/',[StringComparison]::Ordinal)) { throw 'UnexpectedSourcePrefix' }
            Join-Path $projectRoot $name.Substring('ISAFOM-Printer-Installer/'.Length)
        }
        if ((Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -cne $actual) { throw 'ArchiveDiffersFromCurrentSource' }
        $verified++
    }
    Write-Output ('PASS SourceReturn;VerifiedFiles='+$verified+';RequiredHistoricalSources='+$required.Count+';NoPayloadExecuted=True')
} finally { $zip.Dispose() }
