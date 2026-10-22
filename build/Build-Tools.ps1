#requires -Version 7.0
[CmdletBinding()]
param(
 [Parameter(Mandatory)][ValidateSet('Collector','Extractor','Readback','Sentinel')][string]$Tool,
 [string]$Dotnet='', [string]$PackageCache='', [switch]$RestoreOnline
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $Dotnet){$Dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source}
$work=Join-Path $root ('.lavoro-tools/'+$Tool)
New-Item -ItemType Directory -Path $work -Force|Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
if(-not $PackageCache){$PackageCache=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'}
$env:NUGET_PACKAGES=$PackageCache
$env:DOTNET_CLI_HOME=Join-Path $work 'dotnet-home'
$config=Join-Path $work 'NuGet.Config'
if($RestoreOnline){Copy-Item -LiteralPath (Join-Path $root 'NuGet.Config') -Destination $config -Force}
else{[IO.File]::WriteAllText($config,'<configuration><packageSources><clear /></packageSources></configuration>')}
$project=switch($Tool){
 'Collector'{'Isafom.Collector'}
 'Extractor'{'Isafom.ConfigExtractor'}
 'Readback'{'Isafom.ConfigExtractor'}
 'Sentinel'{'Isafom.KxSentinel'}
}
$folder=switch($Tool){
 'Collector'{'collector-0.3.0'}
 'Extractor'{'config-extractor-0.1.2'}
 'Readback'{'kx-reversed-readback-0.1.3'}
 'Sentinel'{'kx-sentinelle-0.1.6'}
}
$output=Join-Path $root ('dist/'+$folder)
$selfContained=if($Tool -eq 'Collector'){'true'}else{'false'}
$extra=@()
if($Tool -ne 'Collector') {
 $targets=Join-Path $work 'Tools.targets'
 [IO.File]::WriteAllText($targets,'<Project><PropertyGroup><SelfContained>false</SelfContained><PublishSingleFile>false</PublishSingleFile><UseAppHost>false</UseAppHost></PropertyGroup></Project>')
 $extra=@('-p:DirectoryBuildTargetsPath='+$targets)
}
Push-Location -LiteralPath $root
try {
 & $Dotnet publish (Join-Path $root ('src/'+$project+'/'+$project+'.csproj')) -c Release -r win-x64 --self-contained $selfContained --artifacts-path (Join-Path $work 'artifacts') --configfile $config -o $output @extra -v:minimal
 if($LASTEXITCODE -ne 0){throw 'ToolBuildFailed'}
 Write-Output ('Compilato: '+$output+'. Nessun tool è stato avviato e nessuna stampante modificata.')
}finally{Pop-Location}
