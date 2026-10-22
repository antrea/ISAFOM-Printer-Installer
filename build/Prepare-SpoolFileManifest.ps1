#requires -Version 7.0
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$names=@(Get-Content -LiteralPath (Join-Path $root 'driver\spool-file-names.txt')|Sort-Object -Unique)
$work=Join-Path $root '.lavoro-installer023\tmp\driver-file-manifest'
$null=New-Item -ItemType Directory -Path $work -Force
$facts=@{}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($version in @('8.5.2405','8.7.0917')){
 $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $root ('driver\KX_'+$version+'_x64.zip')))
 try{
  foreach($name in $names){
   $compressed=$name.Substring(0,$name.Length-1)+'_'
   $entries=@($zip.Entries|Where-Object {$_.FullName -match '/64bit/' -and $_.Name -ieq $name})
   if($entries.Count -eq 0){$entries=@($zip.Entries|Where-Object {$_.FullName -match '/64bit/' -and $_.Name -ieq $compressed})}
   if($entries.Count -eq 0){continue};if($entries.Count -ne 1){throw 'SourceFileNotUnique'}
   $entry=$entries[0]
   $input=$entry.Open()
   try{
    if($entry.Name -ieq $name){$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input))}
    else{
     $packed=Join-Path $work $entry.Name;$expanded=Join-Path $work $name
     $stream=[IO.File]::Create($packed);try{$input.CopyTo($stream)}finally{$stream.Dispose()}
     $null=& "$env:WINDIR\System32\expand.exe" $packed $expanded
     if($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($expanded)){throw 'OfficialExpansionFailed'}
     $hash=(Get-FileHash -LiteralPath $expanded -Algorithm SHA256).Hash
     Remove-Item -LiteralPath $packed,$expanded
    }
    if(-not $facts.ContainsKey($name)){$facts[$name]=@()}
    $facts[$name]+=@($hash)
   }finally{$input.Dispose()}
  }
 }finally{$zip.Dispose()}
}
if($facts.Count -ne $names.Count){throw 'IncompleteFileCoverage'}
$rows=@($facts.Keys|Sort-Object|ForEach-Object {[ordered]@{Name=$_;Hashes=@($facts[$_]|Sort-Object -Unique)}})
ConvertTo-Json -InputObject $rows -Depth 5|Set-Content -LiteralPath (Join-Path $root 'driver\KX_SpoolFiles.json') -Encoding utf8
Write-Output ('Original Kyocera file names covered: '+$rows.Count)
