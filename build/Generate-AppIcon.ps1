#requires -Version 7.0
# Original vector drawing rasterized into PNG frames in a Windows ICO container.
# No external image, package download or system settings required.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Isafom.Windows\Assets'
New-Item -ItemType Directory -Path $folder -Force|Out-Null
$frames=[Collections.Generic.List[object]]::new()
foreach($size in @(16,24,32,48,64,128,256)) {
    $bitmap=[Drawing.Bitmap]::new($size,$size,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform($size/256.0,$size/256.0)
        $path=[Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $path.AddArc(8,8,64,64,180,90);$path.AddArc(184,8,64,64,270,90)
            $path.AddArc(184,184,64,64,0,90);$path.AddArc(8,184,64,64,90,90);$path.CloseFigure()
            $brush=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#087F76'))
            try{$graphics.FillPath($brush,$path)}finally{$brush.Dispose()}
        }finally{$path.Dispose()}
        $paper=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#FFFFFF'))
        $body=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#E0F1EE'))
        $dark=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#143D4C'))
        try {
            $graphics.FillRectangle($paper,72,40,112,58)
            $graphics.FillRectangle($dark,42,88,172,102)
            $graphics.FillRectangle($body,58,101,140,45)
            $graphics.FillRectangle($paper,72,148,112,66)
            $graphics.FillRectangle($dark,91,163,74,7)
            $graphics.FillRectangle($dark,91,181,58,7)
            $graphics.FillEllipse($paper,178,112,11,11)
        }finally{$paper.Dispose();$body.Dispose();$dark.Dispose()}
        $memory=[IO.MemoryStream]::new()
        try {
            $bitmap.Save($memory,[Drawing.Imaging.ImageFormat]::Png)
            $frames.Add([pscustomobject]@{Size=$size;Bytes=$memory.ToArray()})
            if($size -eq 256){[IO.File]::WriteAllBytes((Join-Path $folder 'IsafomPrinter.png'),$memory.ToArray())}
        }finally{$memory.Dispose()}
    }finally{$graphics.Dispose();$bitmap.Dispose()}
}
$stream=[IO.File]::Create((Join-Path $folder 'IsafomPrinter.ico'))
$writer=[IO.BinaryWriter]::new($stream)
try {
    $writer.Write([ushort]0);$writer.Write([ushort]1);$writer.Write([ushort]$frames.Count)
    $offset=6+16*$frames.Count
    foreach($frame in $frames) {
        $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
        $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
        $writer.Write([ushort]1);$writer.Write([ushort]32);$writer.Write([uint]$frame.Bytes.Length);$writer.Write([uint]$offset)
        $offset+=$frame.Bytes.Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
}finally{$writer.Dispose();$stream.Dispose()}
Write-Output 'Icona originale generata: 16, 24, 32, 48, 64, 128 e 256 pixel.'
