#requires -Version 5.1
[CmdletBinding()]
param([string]$ProjectRoot='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=if($ProjectRoot){$ProjectRoot}else{Split-Path -Parent $PSScriptRoot}
foreach($item in @(@('src\Isafom.Windows\Scripts\PrinterBackend.ps1','Test-IsafomCatalog'),@('src\Isafom.Windows\Scripts\RemovalBackend.ps1','Get-IsafomFileHash'))){
 $tokens=$null;$errors=$null
 $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root $item[0]),[ref]$tokens,[ref]$errors)
 if($errors.Count){throw 'ParseError'}
 $name=$item[1]
 $function=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)
 . ([ScriptBlock]::Create($function.Extent.Text))
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$folder=Join-Path $root '.lavoro-installer023\tmp\catalog-check'
$null=New-Item -ItemType Directory -Path $folder -Force
$cat=Join-Path $folder 'kyomita.cat'
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $root 'driver\KX_8.7.0917_x64.zip'))
try{
 $entry=$archive.GetEntry('Kx87_UPD_8.7.0917_en_RC3_signed/64bit/kyomita.cat')
 $a=$entry.Open();$b=[IO.File]::Create($cat)
 try{$a.CopyTo($b)}finally{$a.Dispose();$b.Dispose()}
}finally{$archive.Dispose()}
$script:passed=0
function Check([bool]$condition,[string]$name){if(-not $condition){throw ('FAIL '+$name)};$script:passed++;Write-Output ('PASS '+$name)}
Check (Test-IsafomCatalog $cat) 'Actual original vendor catalogue is trusted and matches both pins'
$actual=Get-AuthenticodeSignature -LiteralPath $cat
function Get-AuthenticodeSignature {param($LiteralPath)[pscustomobject]@{Status='Valid';SignerCertificate=[pscustomobject]@{Thumbprint=('A'*40)}}}
Check (-not (Test-IsafomCatalog $cat)) 'Another trusted publisher cannot replace the supplied signer'
function Get-AuthenticodeSignature {param($LiteralPath)[pscustomobject]@{Status='HashMismatch';SignerCertificate=$actual.SignerCertificate}}
Check (-not (Test-IsafomCatalog $cat)) 'Invalid signature cannot bypass the certificate pin'
function Get-AuthenticodeSignature {param($LiteralPath)$actual}
$bytes=[IO.File]::ReadAllBytes($cat);$bytes[100]=$bytes[100] -bxor 1;[IO.File]::WriteAllBytes($cat,$bytes)
Check (-not (Test-IsafomCatalog $cat)) 'Changed catalogue rejected even if signature provider were to report the original signer'
Remove-Item -LiteralPath $cat
Write-Output ('RESULT '+$passed+' catalogue trust checks passed; no printer changes.')
