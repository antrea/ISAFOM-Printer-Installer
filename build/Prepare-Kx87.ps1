#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OfficialArchive,[ValidateSet('8.5.2405','8.7.0917')][string]$Version='8.7.0917')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$sourceHash=if($Version -eq '8.5.2405'){'54CFBE11ECBB989566EE8ACCD12144E6267A57C3CFB062BA3E2D8DF711502ADA'}else{'E7655FB105B353E7F7F18D6CB88B5CE6CD01FF21E5234471A24DC2B0585DD9A9'}
$prefix=if($Version -eq '8.5.2405'){'Kx85_UPD_8.5.2405_en_RC2_WHQL/'}else{'Kx87_UPD_8.7.0917_en_RC3_signed/'}
if((Get-FileHash -LiteralPath $OfficialArchive -Algorithm SHA256).Hash -cne $sourceHash){throw 'OfficialArchiveIntegrity'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$directory=Join-Path $root 'driver'
New-Item -ItemType Directory -Path $directory -Force|Out-Null
$output=Join-Path $directory ('KX_'+$Version+'_x64.zip')
$inputZip=[IO.Compression.ZipFile]::OpenRead($OfficialArchive)
try {
 $entries=@($inputZip.Entries|Where-Object {
  $_.FullName.StartsWith(($prefix+'64bit/'),'Ordinal') -or
  ($_.FullName.StartsWith($prefix,'Ordinal') -and $_.FullName -match '(?i)(/KX DRIVER[^/]*ReadMe\.rtf|/Setup/EULA/License_(EN|IT)\.rtf)$')
 }|Where-Object {$_.Name.Length -gt 0}|Sort-Object FullName)
 if($entries.Count -lt 600 -or ($entries|Measure-Object Length -Sum).Sum -gt 128MB){throw 'UnexpectedPackageLayout'}
 $manifestEntries=@()
 # Re-create only this exact build output; never expand 32-bit or ARM64 trees.
 $stream=[IO.File]::Open($output,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
 $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
 try {
  foreach($entry in $entries){
   $new=$zip.CreateEntry($entry.FullName,[IO.Compression.CompressionLevel]::Optimal)
   $new.LastWriteTime=$entry.LastWriteTime
   $source=$entry.Open();$target=$new.Open()
   try {$source.CopyTo($target)}finally{$target.Dispose();$source.Dispose()}
  }
 }finally{$zip.Dispose();$stream.Dispose()}
 $result=[IO.Compression.ZipFile]::OpenRead($output)
 try {
  foreach($entry in $result.Entries){
   $original=$inputZip.GetEntry($entry.FullName)
   $a=$entry.Open();$b=$original.Open()
   try {
    $aHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($a))
    $bHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($b))
    if($aHash -cne $bHash -or $entry.Length -ne $original.Length){throw 'ExtractedFileDifference'}
    $manifestEntries+=@{Path=$entry.FullName;Bytes=$entry.Length;Sha256=$aHash}
   }finally{$a.Dispose();$b.Dispose()}
  }
 }finally{$result.Dispose()}
 $hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
 [ordered]@{Version=($Version+'.0');SpoolerVersion=([version]($Version+'.0')).ToString();Architecture='x64';
  OfficialArchiveSha256=$sourceHash;PayloadSha256=$hash;PayloadBytes=(Get-Item -LiteralPath $output).Length;
  AllIncludedFilesIdenticalToOfficialArchive=$true;Entries=$manifestEntries}|ConvertTo-Json -Depth 5|
  Set-Content -LiteralPath (Join-Path $directory ('KX_'+$Version+'_x64.manifest.json')) -Encoding utf8
 Write-Output "Driver x64 preparato: $output"
 Write-Output "SHA256: $hash"
 Write-Output ('File originali verificati: '+$manifestEntries.Count)
}finally{$inputZip.Dispose()}
