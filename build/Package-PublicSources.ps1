#requires -Version 7.0
[CmdletBinding()]
param([string]$Output='')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if(-not $Output){$Output=Join-Path $root 'dist/ISAFOM_Sorgenti_Pubblici_0.2.7-rc1.zip'}
$Output=[IO.Path]::GetFullPath($Output)
if(Test-Path -LiteralPath $Output){throw 'Il pacchetto esiste già: indicare un nuovo percorso, senza sovrascriverlo.'}
Push-Location -LiteralPath $root
try {
 $tracked=@(& git ls-tree -r --name-only HEAD)
 if($LASTEXITCODE -ne 0 -or $tracked.Count -lt 100){throw 'Occorre un commit Git completo.'}
 foreach($path in $tracked){
  if($path -match '(?i)\.(exe|dll|dl_|dl|cat|cab|zip|7z|msi|pfx|key|log)$' -or $path -match '(?i)(^|/)(bin|obj|node_modules|vendor|raccolta-locale|reference-reports|\.lavoro[^/]*)(/|$)'){throw ('Risorsa non pubblicabile tracciata da Git: '+$path)}
 }
 New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force|Out-Null
 & git archive --format=zip ('--output='+$Output) HEAD
 if($LASTEXITCODE -ne 0){throw 'GitArchiveFailed'}
 $zip=[IO.Compression.ZipFile]::OpenRead($Output)
 try{if($zip.Entries.Count -lt $tracked.Count){throw 'PublicArchiveIncomplete'}}finally{$zip.Dispose()}
 Get-FileHash -LiteralPath $Output -Algorithm SHA256|Select-Object Path,Hash
}finally{Pop-Location}
