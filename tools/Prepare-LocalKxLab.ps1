#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$lab=Join-Path $project 'research\local-lab'
New-Item -ItemType Directory -Path $lab -Force|Out-Null
$env:TEMP=Join-Path $lab 'tmp';$env:TMP=$env:TEMP
$env:PSModuleAnalysisCachePath=Join-Path $lab 'ps-cache\ModuleAnalysisCache'
New-Item -ItemType Directory -Path $env:TEMP -Force|Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $env:PSModuleAnalysisCachePath) -Force|Out-Null
$stage='Privileges';$state=$null;$packageAdded=$false;$driverAdded=$false;$unchanged=$null
try{
 $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
 try{if(-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'AdministratorRequired'}}finally{$identity.Dispose()}
 $stage='SourceVerification'
 $source=Join-Path $project 'research\kx85\Kx85_UPD_8.5.2405_en_RC2_WHQL\64bit'
 $inf=Join-Path $source 'OEMSETUP.INF'
 if((Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'kyomita.cat')).Status -ne 'Valid'){throw 'InvalidCatalog'}
 $proof=Get-Content -LiteralPath (Join-Path $lab 'source-proof.json') -Raw|ConvertFrom-Json
 if($proof.VerifiedFiles -lt 600 -or (Get-FileHash -LiteralPath $inf -Algorithm SHA256).Hash -cne $proof.InfSha256){throw 'SourceProofMismatch'}
 if(Test-Path -LiteralPath (Join-Path $lab 'queue-state.json')){throw 'ExistingLabStateRefused'}
 if(@(Get-Printer|Where-Object Name -like 'ISAFOM_TEST_KX_*').Count -ne 0){throw 'PreviousTestQueuePresent'}
 $before=Get-Printer|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
 $driver='Kyocera CS 3554ci KX'
 if(-not(Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)){
  $stage='StageSignedPackage'
  # Add to the driver store only: no /install, device update or reboot.
  $output=& (Join-Path $env:SystemRoot 'System32\pnputil.exe') /add-driver $inf 2>&1
  if($LASTEXITCODE -ne 0){throw 'DriverStoreStagingFailed'}
  $packageAdded=$true
  $stage='RegisterPrinterDriver'
  $stored=@(Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository') -Directory -Filter 'oemsetup.inf_amd64_*'|ForEach-Object {
   $candidate=Join-Path $_.FullName 'OEMSETUP.INF'
   if((Test-Path -LiteralPath $candidate) -and (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ceq $proof.InfSha256){$candidate}
  })
  if($stored.Count -ne 1){throw 'StagedInfNotUnique'}
  Add-PrinterDriver -Name $driver -InfPath $stored[0] -ErrorAction Stop
  $driverAdded=$true
 }
 $stage='CreatePausedFileQueue'
 $token=[Guid]::NewGuid().ToString('N');$queue='ISAFOM_TEST_KX_'+$token.Substring(0,8)
 Add-Printer -Name $queue -DriverName $driver -PortName 'FILE:' -Comment ('ISAFOM KX SENTINEL 0.1.5 '+$token) -ErrorAction Stop
 $instance=Get-CimInstance -ClassName Win32_Printer -Filter ("Name='"+$queue+"'")
 if($null -eq $instance -or (Invoke-CimMethod -InputObject $instance -MethodName Pause).ReturnValue -ne 0){throw 'PauseFailed'}
 $state=[ordered]@{Token=$token;QueueName=$queue;Driver=$driver;Port='FILE:';Paused=$true;Purpose='Local sentinel laboratory only'}
 $state|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $lab 'queue-state.json') -Encoding utf8
 $after=Get-Printer|Where-Object Name -ne $queue|Sort-Object Name|Select-Object Name,DriverName,PortName,Shared|ConvertTo-Json -Compress
 $unchanged=$before -ceq $after
 $stage='Completed'
}catch{
 # Exception text and native output are deliberately not persisted.
}
[ordered]@{Stage=$stage;Completed=($stage -eq 'Completed');PackageAdded=$packageAdded;DriverAdded=$driverAdded;LabQueueCreated=($null -ne $state);OtherPrinterMetadataUnchanged=$unchanged;PhysicalPrintPerformed=$false;SpoolerRestarted=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $lab 'setup-result.json') -Encoding utf8
