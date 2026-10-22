#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$workRoot=Split-Path -Parent $projectRoot
try {
    if(-not(Test-Path -LiteralPath (Join-Path $workRoot 'LEGGIMI_SENTINELLE_KX.txt') -PathType Leaf)){throw 'PortableWorkspaceRequired'}
    $env:TEMP=Join-Path $workRoot '.lavoro\tmp';$env:TMP=$env:TEMP
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $workRoot '.lavoro\bundle'
    $env:PSModuleAnalysisCachePath=Join-Path $workRoot '.lavoro\ps-cache\ModuleAnalysisCache'
    foreach($path in @($env:TEMP,$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR,(Split-Path -Parent $env:PSModuleAnalysisCachePath))){New-Item -ItemType Directory -Path $path -Force|Out-Null}
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{$isAdmin=([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}finally{$identity.Dispose()}
    if(-not $isAdmin){
        Write-Host 'La prova crea e rimuove solo una coda separata. Windows richiede i privilegi amministrativi.'
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'"'
        # This is the interactive test console the user needs to inspect.
        Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -Verb RunAs -Wait
        return
    }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
    $dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
    $entry=Join-Path $projectRoot 'dist\kx-sentinelle-0.1.6\Isafom.KxSentinel.dll'
    if(-not(Test-Path -LiteralPath $entry -PathType Leaf)){throw 'BuildMissing'}
    & $dotnet $entry --sentinel-trial
    if($LASTEXITCODE -ne 0){Write-Host 'Prova non completamente verificata: condividere il solo JSON nella cartella risultati.'}
} catch {
    Write-Host 'Avvio non riuscito. Servono Windows x64, .NET 10 Desktop e il driver KX 8.5 originale già installato. Nessun driver viene scaricato.'
}
[void](Read-Host 'Premi INVIO per chiudere questa finestra')
