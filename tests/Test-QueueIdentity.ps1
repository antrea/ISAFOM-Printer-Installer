#requires -Version 5.1
[CmdletBinding()]
param([string]$ProjectRoot='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=if($ProjectRoot){$ProjectRoot}else{Split-Path -Parent $PSScriptRoot}
$file=Join-Path $root 'src\Isafom.Windows\Scripts\PrinterBackend.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'BackendParseFailed'}
# Load only these read-only functions, never the provisioning entry point.
foreach($name in @('Get-IsafomOwnerMarker','Test-IsafomQueueIdentity','Test-IsafomManagedQueue','Test-IsafomWindows')) {
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true)
    if($null -eq $function){throw 'MissingIdentityFunction'}
    . ([ScriptBlock]::Create($function.Extent.Text))
}
$script:passed=0
function Check([bool]$condition,[string]$label) {
    if(-not $condition){throw ('FAIL '+$label)}
    $script:passed++;Write-Output ('PASS '+$label)
}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$marker=Get-IsafomOwnerMarker $sid
$author='Autore: Andrea Esposito - andrea.esposito@cnr.it'
Check (Test-IsafomQueueIdentity $marker $null $marker) 'Legacy completed queue recognized'
Check (Test-IsafomQueueIdentity ($marker+' pending') $null $marker) 'Legacy pending queue recognized'
Check (Test-IsafomQueueIdentity $author $marker $marker) 'Author comment plus hidden owner recognized'
Check (-not (Test-IsafomQueueIdentity $author $null $marker)) 'Author text without owner refused'
Check (-not (Test-IsafomQueueIdentity $author (Get-IsafomOwnerMarker 'S-1-5-21-1-2-3-9999') $marker)) 'Foreign owner refused'
Check (-not (Test-IsafomQueueIdentity 'Other queue' $marker $marker)) 'Unrelated comment refused'
Check (-not (Test-IsafomManagedQueue ('ISAFOM_MISSING_'+[Guid]::NewGuid().ToString('N')) $author $marker)) 'Missing printer metadata fails closed'
foreach($build in @(14393,17763,19044,19045,22000,22631,26100)){Check (Test-IsafomWindows $build 1) ('Windows 10/11 client build '+$build+' accepted')}
Check (-not (Test-IsafomWindows 9600 1)) 'Windows 8.1 refused'
Check (-not (Test-IsafomWindows 10240 1)) 'Windows 10 older than runtime baseline refused'
Check (-not (Test-IsafomWindows 26100 3)) 'Windows Server refused'
Write-Output ('RESULT '+$script:passed+' backend identity checks passed; no printer changes.')
