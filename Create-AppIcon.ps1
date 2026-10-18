$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetPath = Join-Path $PSScriptRoot 'app\Assets'
New-Item -ItemType Directory -Path $assetPath -Force | Out-Null
$iconBitmap = New-Object Drawing.Bitmap(256,256)
$iconGraphics = [Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$iconGraphics.Clear([Drawing.Color]::FromArgb(12,12,13))
$iconBrush = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(232,68,63))
$iconShape = New-Object Drawing.Drawing2D.GraphicsPath
$iconShape.AddRectangle((New-Object Drawing.Rectangle(24,24,208,208)))
$framePen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(232,68,63),14)
$iconGraphics.DrawRectangle($framePen,24,24,208,208)
$framePen.Dispose()
$iconPen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(240,234,219),13)
$iconPen.StartCap = $iconPen.EndCap = [Drawing.Drawing2D.LineCap]::Square
$iconPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Miter
$iconGraphics.DrawLine($iconPen,82,185,82,81)
$iconGraphics.DrawLine($iconPen,82,81,181,81)
$iconGraphics.DrawLine($iconPen,82,137,181,137)
$iconGraphics.DrawLines($iconPen,[Drawing.Point[]]@((New-Object Drawing.Point(157,57)),(New-Object Drawing.Point(181,81)),(New-Object Drawing.Point(157,105))))
$iconGraphics.DrawLines($iconPen,[Drawing.Point[]]@((New-Object Drawing.Point(157,113)),(New-Object Drawing.Point(181,137)),(New-Object Drawing.Point(157,161))))
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
