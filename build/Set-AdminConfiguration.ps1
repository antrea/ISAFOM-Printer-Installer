#requires -Version 7.0
#requires -RunAsAdministrator
# Explicit administrative operation. Never called by diagnostics or build.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ConfigurationFile,
    [string]$Executable = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $root 'dist\installer-0.2.7-rc1\Installa_Stampante_ISAFOM.exe' }
$source = [IO.Path]::GetFullPath($ConfigurationFile)
if ([IO.FileInfo]::new($source).Length -gt 16384) { throw 'Configurazione troppo grande.' }
$settingsText = [IO.File]::ReadAllText($source)
if ($settingsText.Length -gt 16384) { throw 'Configurazione troppo grande.' }
$start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
$start.ArgumentList.Add('--validate-config-stdin')
$process = [Diagnostics.Process]::Start($start)
$process.StandardInput.Write($settingsText)
$process.StandardInput.Close()
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw 'Configurazione rifiutata dalla applicazione. Nessun file di sistema modificato.' }
# Only the validated in-memory text is written; no secret-shaped unknown property can reach a temp file.
$directory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'CNR-ISAFOM'
$admin = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$trusted = @($admin.Value,$system.Value,'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
if (-not (Test-Path -LiteralPath $directory)) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.SetOwner($admin)
    foreach ($sid in @($admin,$system)) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit, ObjectInherit','None','Allow'))
    }
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','ContainerInherit, ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($directory),$security)
}
for ($current = $directory; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if ([IO.File]::GetAttributes($current).HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Percorso amministrativo non sicuro.' }
}
$acl = Get-Acl -LiteralPath $directory
if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Proprietario cartella non attendibile.' }
$writeMask = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor
    [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
    [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
    [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership
foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
    if ($rule.AccessControlType -eq 'Allow' -and ($rule.FileSystemRights -band $writeMask) -ne 0 -and $rule.IdentityReference.Value -notin $trusted) { throw 'Cartella scrivibile da soggetti non attendibili.' }
}
$temporary = Join-Path $directory ('settings-' + [Guid]::NewGuid().ToString('N') + '.json')
$target = Join-Path $directory 'printer-settings.json'
if ((Test-Path -LiteralPath $target) -and [IO.File]::GetAttributes($target).HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'File amministrativo non sicuro.' }
[IO.File]::WriteAllText($temporary,$settingsText,[Text.UTF8Encoding]::new($false))
$fileAcl = Get-Acl -LiteralPath $temporary
$fileAcl.SetOwner($admin)
Set-Acl -LiteralPath $temporary -AclObject $fileAcl
[IO.File]::Move($temporary,$target,$true)
Write-Output 'Configurazione amministrativa aggiornata. Nessuna stampante o preferenza personale è stata modificata.'
