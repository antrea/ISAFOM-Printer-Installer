#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path -Parent $PSScriptRoot
$source=Join-Path $root 'src\Isafom.Windows\Assets\IsafomPrinter.ico'
$reader=[IO.BinaryReader]::new([IO.File]::OpenRead($source))
try {
    if($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1 -or $reader.ReadUInt16() -ne 7){throw 'InvalidIconContainer'}
    $sizes=foreach($index in 0..6){$width=$reader.ReadByte();$height=$reader.ReadByte();[void]$reader.ReadBytes(14);if($width -ne $height){throw 'InvalidIconFrame'};if($width -eq 0){256}else{[int]$width}}
    if(($sizes -join ',') -cne '16,24,32,48,64,128,256'){throw 'MissingIconResolutions'}
}finally{$reader.Dispose()}
$expected=[Drawing.Icon]::new($source,32,32)
$actual=[Drawing.Icon]::ExtractAssociatedIcon([IO.Path]::GetFullPath($Executable))
if($null -eq $actual){throw 'ExecutableIconMissing'}
try {
    $left=$expected.ToBitmap();$right=$actual.ToBitmap()
    try {
        if($left.Size -ne $right.Size){throw 'UnexpectedShellIconSize'}
        $difference=0L
        foreach($y in 0..($left.Height-1)){foreach($x in 0..($left.Width-1)){
            $a=$left.GetPixel($x,$y);$b=$right.GetPixel($x,$y)
            $difference += [Math]::Abs([int]$a.A-$b.A)+[Math]::Abs([int]$a.R-$b.R)+[Math]::Abs([int]$a.G-$b.G)+[Math]::Abs([int]$a.B-$b.B)
        }}
        if($difference/($left.Width*$left.Height*4) -gt 1){throw 'ExecutableUsesAnotherIcon'}
        New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
        $right.Save((Join-Path $OutputDirectory 'Icona_Estratta_EXE.png'),[Drawing.Imaging.ImageFormat]::Png)
        [ordered]@{Passed=$true;SourceFrameSizes=$sizes;ExecutableIconMatches=$true;IconWidth=$right.Width;IconHeight=$right.Height}|ConvertTo-Json -Depth 3|Set-Content -LiteralPath (Join-Path $OutputDirectory 'icon-validation.json') -Encoding utf8
    }finally{$left.Dispose();$right.Dispose()}
}finally{$expected.Dispose();$actual.Dispose()}
Write-Output 'PASS multi-size ICO and icon extracted from the published EXE match.'
