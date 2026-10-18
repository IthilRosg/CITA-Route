$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path $PSScriptRoot 'app\Assets'
New-Item -ItemType Directory -Path $assetPath -Force | Out-Null
$iconBitmap = New-Object Drawing.Bitmap(256,256)
$iconGraphics = [Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.Clear([Drawing.Color]::Transparent)
$iconShape = New-Object Drawing.Drawing2D.GraphicsPath
$iconShape.AddArc(8,8,88,88,180,90)
$iconShape.AddArc(160,8,88,88,270,90)
$iconShape.AddArc(160,160,88,88,0,90)
$iconShape.AddArc(8,160,88,88,90,90)
$iconShape.CloseFigure()
$iconBrush = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(192,239,134))
$iconGraphics.FillPath($iconBrush,$iconShape)
$iconPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(20,35,24),14)
$iconPen.StartCap = $iconPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$iconPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
$iconGraphics.DrawLine($iconPen,72,194,72,72)
$iconGraphics.DrawLine($iconPen,72,72,186,72)
$iconGraphics.DrawLine($iconPen,72,136,186,136)
$iconGraphics.DrawLines($iconPen,[Drawing.Point[]]@((New-Object Drawing.Point(158,44)),(New-Object Drawing.Point(186,72)),(New-Object Drawing.Point(158,100))))
$iconGraphics.DrawLines($iconPen,[Drawing.Point[]]@((New-Object Drawing.Point(158,108)),(New-Object Drawing.Point(186,136)),(New-Object Drawing.Point(158,164))))
$iconPng = New-Object IO.MemoryStream
$iconBitmap.Save($iconPng,[Drawing.Imaging.ImageFormat]::Png)
$iconPngBytes = $iconPng.ToArray()
$iconFile = [IO.File]::Create((Join-Path $assetPath 'RoutePilot.ico'))
$iconWriter = New-Object IO.BinaryWriter($iconFile)
$iconWriter.Write([uint16]0); $iconWriter.Write([uint16]1); $iconWriter.Write([uint16]1)
$iconWriter.Write([byte]0); $iconWriter.Write([byte]0); $iconWriter.Write([byte]0); $iconWriter.Write([byte]0)
$iconWriter.Write([uint16]1); $iconWriter.Write([uint16]32); $iconWriter.Write([uint32]$iconPngBytes.Length); $iconWriter.Write([uint32]22)
$iconWriter.Write($iconPngBytes)
$iconWriter.Dispose(); $iconGraphics.Dispose(); $iconBitmap.Dispose(); $iconBrush.Dispose(); $iconPen.Dispose(); $iconShape.Dispose(); $iconPng.Dispose()
