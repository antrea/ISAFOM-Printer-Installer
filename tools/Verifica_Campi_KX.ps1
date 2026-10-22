#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$workRoot=Split-Path -Parent $projectRoot
$env:TEMP=Join-Path $workRoot '.lavoro\tmp';$env:TMP=$env:TEMP
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $workRoot '.lavoro\bundle'
$env:PSModuleAnalysisCachePath=Join-Path $workRoot '.lavoro\ps-cache\ModuleAnalysisCache'
$process=$null;$secureCode=$null;$secretPtr=[IntPtr]::Zero
try {
    foreach($folder in @($env:TEMP,$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR,(Split-Path -Parent $env:PSModuleAnalysisCachePath))){New-Item -ItemType Directory -Path $folder -Force|Out-Null}
    $entry=Join-Path $projectRoot 'dist\kx-reversed-readback-0.1.3\Isafom.ConfigExtractor.dll'
    if(-not(Test-Path -LiteralPath $entry -PathType Leaf)){throw 'BuildMissing'}
    $dotnet=(Get-Command dotnet.exe -ErrorAction Stop).Source
    Write-Host 'Verifica sperimentale KX 8.5, in sola lettura. Nessuna installazione o stampa.'
    $expectedName=Read-Host 'Nome utente già impostato nel driver'
    $secureCode=Read-Host 'Account ID / codice privato già impostato' -AsSecureString
    $secretPtr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureCode)
    $expectedCode=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPtr)
    if([string]::IsNullOrWhiteSpace($expectedName) -or $expectedName.Length -gt 128 -or $expectedCode -cnotmatch '^[0-9]{1,8}$'){throw 'InvalidInput'}
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$dotnet
    $start.Arguments='"'+$entry+'" --verify-kx85-reversed'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
    $process=[Diagnostics.Process]::Start($start)
    $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
    $request=[ordered]@{UserName=$expectedName;AccountId=$expectedCode}|ConvertTo-Json -Compress
    # Windows PowerShell 5.1 has no ProcessStartInfo.StandardInputEncoding.
    # Write UTF-8 directly to the private pipe, independent of the OEM codepage.
    $requestBytes=[Text.Encoding]::UTF8.GetBytes($request+"`n")
    try{$process.StandardInput.BaseStream.Write($requestBytes,0,$requestBytes.Length);$process.StandardInput.BaseStream.Flush()}
    finally{[Array]::Clear($requestBytes,0,$requestBytes.Length);$process.StandardInput.Close()}
    $request=$null;$expectedCode=$null;$expectedName=$null
    Write-Host 'Lettura e confronto in corso...'
    if(-not $process.WaitForExit(90000)){ $process.Kill();throw 'ReadTimeout' }
    $raw=$stdout.GetAwaiter().GetResult();[void]$stderr.GetAwaiter().GetResult()
    if($raw.Length -gt 16384){throw 'InvalidReply'}
    $report=$raw|ConvertFrom-Json
    # Fixed field names only. Never save a generic response or arbitrary strings.
    $safe=[ordered]@{SchemaVersion=1;ToolVersion='0.1.3-research'}
    foreach($field in @('ExactDriverComponentVerified','SetupLayoutRecognized','NameMatchesUtf8','NameMatchesWindowsCodePage','AccountMatches','AccountingEnabled','SpecificAccountSelected','PrivateCodeCandidateMatches','PrivateRepresentationRecognized','WithinObservedFourDigitLimit','PerUserOverridesVerified','InstallerReady')){
        $property=$report.PSObject.Properties[$field]
        $value=if($null -ne $property){$property.Value}else{$null}
        if($null -ne $value -and $value -isnot [bool]){throw 'InvalidReplyType'}
        $safe[$field]=$value
    }
    $safe['ReadOnly']=$true;$safe['InstallerReady']=$false
    $reasonProperty=$report.PSObject.Properties['Reason']
    $allowedReasons=@('TargetQueueNotUnique','TargetEndpointNotVerified','ExactOriginalKx85FileRequired','ExactDriverComponentNotFound','DriverDependencyListUnavailable','DriverMetadataUnavailable','PrinterReadUnavailable','InvalidInput','ReadUnavailable')
    $safe['Reason']=if($null -ne $reasonProperty -and $allowedReasons -ccontains $reasonProperty.Value){$reasonProperty.Value}elseif($process.ExitCode -eq 3){'ReadCompleted'}else{'ReadUnavailable'}
    $safe['Limite']='Solo campi della coda globale; override personali e stampa fisica non certificati. Un codice privato non riconosciuto resta non verificabile.'
    $folder=Join-Path $workRoot 'raccolta-locale';New-Item -ItemType Directory -Path $folder -Force|Out-Null
    $output=Join-Path $folder ('Verifica_Campi_KX_'+(Get-Date -Format 'yyyyMMdd_HHmmss_fff')+'.json')
    $bytes=[Text.Encoding]::UTF8.GetBytes(($safe|ConvertTo-Json -Depth 3))
    $currentBytes=(Get-ChildItem -LiteralPath $workRoot -File -Recurse -Force|Measure-Object Length -Sum).Sum
    if($currentBytes+$bytes.Length -gt 500MB){throw 'WorkspaceBudgetExceeded'}
    $stream=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    Write-Host 'Verifica completata. True = corrisponde; null = non verificabile. Il codice privato indica un match candidato.'
    $safe|ConvertTo-Json -Depth 3|Write-Host
    Write-Host ('Rapporto senza dati inseriti: '+$output)
} catch {
    Write-Host 'Verifica non completata o non verificabile su questo PC. Nessuna impostazione modificata. Controllare runtime .NET 10, coda ISAFOM e componente KX 8.5 originale.'
} finally {
    if($secretPtr -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPtr)}
    if($null -ne $secureCode){$secureCode.Dispose()}
    if($null -ne $process){
        try{if(-not $process.HasExited){$process.Kill()}}catch{}
        $process.Dispose()
    }
}
