#requires -Version 5.1
[CmdletBinding()]
param([switch]$Sentinella,[switch]$VerificaIdentita)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
try {
    $projectRoot=Split-Path -Parent $PSScriptRoot
    $workRoot=Split-Path -Parent $projectRoot
    $env:TEMP=Join-Path $workRoot '.lavoro\tmp';$env:TMP=$env:TEMP
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $workRoot '.lavoro\bundle'
    $env:DOTNET_CLI_HOME=Join-Path $workRoot '.lavoro\dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
    $env:PSModuleAnalysisCachePath=Join-Path $workRoot '.lavoro\ps-cache\ModuleAnalysisCache'
    $entry=Join-Path $projectRoot 'dist\config-extractor-0.1.2\Isafom.ConfigExtractor.dll'
    if(-not(Test-Path -LiteralPath $entry -PathType Leaf)){throw 'ToolBuildMissing'}
    if($Sentinella -and $VerificaIdentita){throw 'MutuallyExclusiveModes'}
    $dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
    if($Sentinella){& $dotnet $entry --sentinel;exit $LASTEXITCODE}
    if($VerificaIdentita){& $dotnet $entry --verify-identity-ui;if($LASTEXITCODE -ne 0){throw 'VerificationStartupFailed'};exit 0}
    $relative='raccolta-locale\Configurazione_Kyocera_'+(Get-Date -Format 'yyyyMMdd_HHmmss_fff')
    & $dotnet $entry --output-dir (Join-Path $workRoot $relative)
    $result=$LASTEXITCODE
    if($result -eq 0){Write-Output ('Rapporto e ZIP: '+$relative)}
    elseif($result -eq 3){Write-Output 'Rilevate differenze durante la lettura: esaminare il rapporto prima di proseguire.'}
    exit $result
} catch {
    if($VerificaIdentita){
        Add-Type -AssemblyName PresentationFramework
        [void][Windows.MessageBox]::Show('Impossibile avviare la verifica. Controlla che il progetto sia estratto e che .NET 10 Windows Desktop sia installato.','ISAFOM — Verifica Kyocera',[Windows.MessageBoxButton]::OK,[Windows.MessageBoxImage]::Warning)
    }
    else{Write-Output ('Avvio non completato: '+$_.Exception.GetType().Name+'. Nessun download o installazione automatica.')}
    exit 1
}
