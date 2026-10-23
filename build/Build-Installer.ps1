#requires -Version 7.0
[CmdletBinding()]
param([switch]$SkipTests,[switch]$RestoreOnline,[string]$PackageCache='',[string]$Dotnet='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'Test-DriverPayloads.ps1')
$work=Join-Path $root '.lavoro-installer023'
New-Item -ItemType Directory -Path $work -Force|Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
if(-not $PackageCache){$PackageCache=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'}
$env:NUGET_PACKAGES=$PackageCache
$env:DOTNET_CLI_HOME=Join-Path $work 'dotnet-home'
$env:TEMP=Join-Path $work 'tmp';$env:TMP=$env:TEMP
New-Item -ItemType Directory -Path $env:TEMP -Force|Out-Null
$sdk=$Dotnet
if(-not $sdk){$sdk=Join-Path $PSScriptRoot 'dotnet\dotnet.exe'}
if(-not(Test-Path -LiteralPath $sdk)){$sdk=(Get-Command dotnet.exe -ErrorAction Stop).Source}
$config=Join-Path $work 'NuGet.Config'
$targets=Join-Path $work 'Tests.targets'
if($RestoreOnline){Copy-Item -LiteralPath (Join-Path $root 'NuGet.Config') -Destination $config -Force}
else{[IO.File]::WriteAllText($config,'<configuration><packageSources><clear /></packageSources></configuration>')}
[IO.File]::WriteAllText($targets,'<Project><PropertyGroup><SelfContained>false</SelfContained><PublishSingleFile>false</PublishSingleFile><UseAppHost>false</UseAppHost><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>')
if(-not $SkipTests){
 & (Join-Path $root 'tests\Test-QueueIdentity.ps1')
 & (Join-Path $root 'tests\Test-DriverCatalog.ps1')
 & (Join-Path $root 'tests\Test-RemovalBackend.ps1')
 & (Join-Path $root 'tests\Test-ForcedRemoval.ps1')
 foreach($project in @('Isafom.Tests','Isafom.Windows.Tests','Isafom.Installer.Tests')) {
  $file=Join-Path $root ('tests\'+$project+'\'+$project+'.csproj')
  $artifacts=Join-Path $work $project
  & $sdk build $file -c Release --artifacts-path $artifacts --configfile $config "-p:DirectoryBuildTargetsPath=$targets" -v:minimal
  if($LASTEXITCODE -ne 0){throw 'TestsBuildFailed'}
  $framework=if($project -eq 'Isafom.Tests'){'release'}else{'release_win-x64'}
  & $sdk (Join-Path $artifacts ('bin\'+$project+'\'+$framework+'\'+$project+'.dll'))
  if($LASTEXITCODE -ne 0){throw 'TestsFailed'}
  if($project -eq 'Isafom.Installer.Tests') {
   & $sdk (Join-Path $artifacts ('bin\'+$project+'\'+$framework+'\'+$project+'.dll')) --gui (Join-Path $work 'gui-checks')
   if($LASTEXITCODE -ne 0){throw 'GuiDpiTestsFailed'}
   & $sdk (Join-Path $artifacts ('bin\'+$project+'\'+$framework+'\'+$project+'.dll')) --selective-live
   if($LASTEXITCODE -ne 0){throw 'SelectiveInventoryTestsFailed'}
  }
 }
}
$destination=Join-Path $root 'dist\installer-0.2.7-rc1'
& $sdk publish (Join-Path $root 'src\Isafom.Windows\Isafom.Windows.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $work 'publish') --configfile $config -o $destination -v:minimal
if($LASTEXITCODE -ne 0){throw 'PublishFailed'}
$exe=Join-Path $destination 'Installa_Stampante_ISAFOM.exe'
if(@(Get-ChildItem -LiteralPath $destination -File).Count -ne 1){throw 'UnexpectedPublishFiles'}
('{0}  Installa_Stampante_ISAFOM.exe' -f (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash)|Set-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') -Encoding utf8
Write-Output ('Installer compilato: '+$exe)
Write-Output 'La prova fisica e il riavvio restano separati dai controlli locali. Nessuna stampa eseguita dalla build.'
