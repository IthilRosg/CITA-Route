param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach($size in @(@(164,314,'welcome.bmp'),@(55,55,'mark.bmp'))){
    $bitmap=New-Object Drawing.Bitmap([int]$size[0],[int]$size[1])
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    $red=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#E8443F'))
    $cream=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#F0EADB'))
    $dim=New-Object Drawing.SolidBrush([Drawing.ColorTranslator]::FromHtml('#AAA297'))
    $font=New-Object Drawing.Font('Consolas',22,[Drawing.FontStyle]::Bold)
    $small=New-Object Drawing.Font('Consolas',8)
    try{
        $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#0C0C0C'))
        $graphics.FillRectangle($red,0,0,4,[int]$size[1])
        if($size[0] -gt 100){
            $graphics.DrawString('CITA',$font,$cream,15,32)
            $graphics.DrawString('/ ROUTE',$font,$cream,15,65)
            $graphics.FillRectangle($red,18,116,125,4)
            $graphics.DrawString("NETWORK`nCONSOLE`n`nDIRECT / DPI`nVPN",$small,$dim,18,143)
            $graphics.DrawString('SYSTEM READY',$small,$red,18,282)
        }else{$graphics.DrawString('C/',$font,$red,6,10)}
        $bitmap.Save((Join-Path $OutputDirectory $size[2]),[Drawing.Imaging.ImageFormat]::Bmp)
    }finally{$font.Dispose();$small.Dispose();$dim.Dispose();$cream.Dispose();$red.Dispose();$graphics.Dispose();$bitmap.Dispose()}
}
