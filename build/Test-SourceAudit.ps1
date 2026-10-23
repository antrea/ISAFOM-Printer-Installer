#requires -Version 7.0
[CmdletBinding()]
param([string]$Dotnet='dotnet.exe')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$work=Join-Path $root '.lavoro-source-audit'
New-Item -ItemType Directory -Path $work -Force|Out-Null
$targets=Join-Path $work 'SourceAudit.targets'
# Compile the public sources without fabricating vendor archives. These outputs
# are test assemblies only; publishing via this target is explicitly refused.
[IO.File]::WriteAllText($targets,@'
<Project>
 <PropertyGroup><SelfContained>false</SelfContained><PublishSingleFile>false</PublishSingleFile><UseAppHost>false</UseAppHost></PropertyGroup>
 <ItemGroup><EmbeddedResource Remove="../../driver/KX_8.7.0917_x64.zip;../../driver/KX_8.5.2405_x64.zip" /></ItemGroup>
 <Target Name="RefuseAuditPublish" BeforeTargets="Publish"><Error Text="Source audit excludes proprietary payloads and cannot be published. Use Build-Installer.ps1." /></Target>
</Project>
'@)
function Invoke-Checked([string]$Command,[string[]]$Arguments) {
    & $Command @Arguments
    if($LASTEXITCODE -ne 0){throw ('Audit command failed: '+$Command+' (exit '+$LASTEXITCODE+')')}
}
Push-Location $root
try {
    Write-Output 'SKIP Vendor payload/catalog integrity tests: official archives are required by Build-Installer.ps1.'
    foreach($test in @('Test-QueueIdentity.ps1','Test-RemovalBackend.ps1','Test-ForcedRemoval.ps1')) {
        & (Join-Path $root ('tests\'+$test))
    }
    # Execute the same mocked removal tests with the engine embedded by the app.
    foreach($test in @('Test-RemovalBackend.ps1','Test-ForcedRemoval.ps1')) {
        $path=(Join-Path $root ('tests\'+$test)).Replace("'","''")
        $literalRoot=$root.Replace("'","''")
        $code="& ([ScriptBlock]::Create([IO.File]::ReadAllText('$path'))) -ProjectRoot '$literalRoot'"
        Invoke-Checked 'powershell.exe' @('-NoProfile','-NonInteractive','-EncodedCommand',[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code)))
    }
    foreach($project in @('Isafom.Tests','Isafom.Windows.Tests','Isafom.Installer.Tests')) {
        $artifacts=Join-Path $work $project
        Invoke-Checked $Dotnet @('build',('tests\'+$project+'\'+$project+'.csproj'),'-c','Release','--artifacts-path',$artifacts,
            '--configfile',(Join-Path $root 'NuGet.Config'),('-p:DirectoryBuildTargetsPath='+$targets),'-v:minimal')
        $framework=if($project -eq 'Isafom.Tests'){'release'}else{'release_win-x64'}
        $assembly=Join-Path $artifacts ('bin\'+$project+'\'+$framework+'\'+$project+'.dll')
        if($project -eq 'Isafom.Installer.Tests') {
            Invoke-Checked $Dotnet @($assembly,'--source-audit')
            Invoke-Checked $Dotnet @($assembly,'--gui',(Join-Path $work 'gui'))
            Invoke-Checked $Dotnet @($assembly,'--selective-live')
        } else {Invoke-Checked $Dotnet @($assembly)}
    }
    Write-Output 'Source audit passed. Vendor payloads/publication and destructive native removal are not part of this audit.'
} finally {Pop-Location}
