#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('A','B')][string]$SentinelSet='A')
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$lab=Join-Path $project 'research\local-lab'
$env:TEMP=Join-Path $lab 'tmp';$env:TMP=$env:TEMP
$state=Get-Content -LiteralPath (Join-Path $lab 'queue-state.json') -Raw|ConvertFrom-Json
$dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
$entry=Join-Path $project 'dist\kx-sentinelle-0.1.6\Isafom.KxSentinel.dll'
$beforePrinters=Get-Printer|Where-Object Name -ne $state.QueueName|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
$beforeDefault=(Get-CimInstance -ClassName Win32_Printer -Filter 'Default=TRUE').Name
function Invoke-LabChild([string]$Action,[string]$Request){
 $start=[Diagnostics.ProcessStartInfo]::new($dotnet)
 $start.ArgumentList.Add($entry);$start.ArgumentList.Add($Action);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $start.StandardInputEncoding=[Text.UTF8Encoding]::new($false);$start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
 $child=[Diagnostics.Process]::Start($start)
 try{
  $out=$child.StandardOutput.ReadToEndAsync();$err=$child.StandardError.ReadToEndAsync()
  $child.StandardInput.WriteLine($Request);$child.StandardInput.Close()
  if(-not $child.WaitForExit(90000)){$child.Kill();throw 'ChildTimeout'}
  $json=$out.GetAwaiter().GetResult();[void]$err.GetAwaiter().GetResult()
  if($json.Length -gt 16384){throw 'ReplyTooLarge'}
  return [pscustomobject]@{Result=($json|ConvertFrom-Json);ExitCode=$child.ExitCode}
 }finally{$child.Dispose()}
}
$baseline=$null
for($attempt=0;$attempt -lt 10;$attempt++){
 $code='0'+([Security.Cryptography.RandomNumberGenerator]::GetInt32(1000).ToString('D3'))
 $request=[ordered]@{Token=$state.Token;UserName=('ISAFOM_SENTINELLA_'+$SentinelSet);AccountId=$code}|ConvertTo-Json -Compress
 $baseline=Invoke-LabChild '--local-lab-readback' $request
 if($baseline.ExitCode -ne 0){throw 'BaselineUnavailable'}
 if($baseline.Result.Readback.AccountMatches -eq $false){break}
}
if($baseline.Result.Readback.AccountMatches -ne $false){throw 'DistinctSyntheticCodeUnavailable'}
$safe=[ordered]@{SchemaVersion=2;ToolVersion='0.1.6-research';LocalLab=$true;PhysicalPrintPerformed=$false;ExpectedValuesExported=$false;RequestedCodeDistinctFromPrevious=$true;Before=$baseline.Result}
$native=Invoke-LabChild '--native-write-worker' $request
$safe['Native']=$native.Result;$safe['NativeExitCode']=$native.ExitCode
$read=Invoke-LabChild '--local-lab-readback' $request
$safe['IndependentReadback']=$read.Result;$safe['IndependentReadbackExitCode']=$read.ExitCode
$afterPrinters=Get-Printer|Where-Object Name -ne $state.QueueName|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
$afterDefault=(Get-CimInstance -ClassName Win32_Printer -Filter 'Default=TRUE').Name
$safe['OtherPrinterMetadataUnchanged']=$beforePrinters -ceq $afterPrinters
$safe['DefaultPrinterUnchanged']=$beforeDefault -ceq $afterDefault
$request=$null;$code=$null
$path=Join-Path $lab ('native-probe-'+(Get-Date -Format 'yyyyMMdd_HHmmss')+'.json')
$safe|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
$safe|ConvertTo-Json -Depth 8
