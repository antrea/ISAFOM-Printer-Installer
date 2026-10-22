#requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$pins=@{
 '8.5.2405'='7100E66596C06252621B604D6CF19EA30527949D56319D83873909A66FC3092E'
 '8.7.0917'='C1ABC58594979E2B38B8344B8AB7DF3DBDFC987DFFDF3FF02BB5F6FC289DCD8F'
}
foreach($version in @('8.5.2405','8.7.0917')) {
 $path=Join-Path $root ('driver/KX_'+$version+'_x64.zip')
 if(-not(Test-Path -LiteralPath $path -PathType Leaf)) {
  throw ('Payload '+$version+' assente. Leggere driver/README.md e prepararlo con build/Prepare-Kx87.ps1 dall''archivio ufficiale. Nessun download automatico.')
 }
 if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $pins[$version]) {
  throw ('Integrità payload '+$version+' non corrispondente al profilo autorizzato. Non modificare le impronte per aggirare il controllo.')
 }
 Write-Output ('PASS payload x64 '+$version+' — SHA-256 corrispondente al profilo')
}
