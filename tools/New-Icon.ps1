param([Parameter(Mandatory=$true)][string]$SourcePng,[Parameter(Mandatory=$true)][string]$DestinationIco)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$sizes=@(16,20,24,32,40,48,64,128,256)
$source=[System.Drawing.Image]::FromFile($SourcePng)
try {
  $images=[System.Collections.Generic.List[byte[]]]::new()
  foreach($size in $sizes){
    $bitmap=[System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{$graphics=[System.Drawing.Graphics]::FromImage($bitmap);try{$graphics.Clear([System.Drawing.Color]::Transparent);$graphics.CompositingQuality=[System.Drawing.Drawing2D.CompositingQuality]::HighQuality;$graphics.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$graphics.SmoothingMode=[System.Drawing.Drawing2D.SmoothingMode]::HighQuality;$graphics.PixelOffsetMode=[System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality;$graphics.DrawImage($source,0,0,$size,$size)}finally{$graphics.Dispose()};$memory=[System.IO.MemoryStream]::new();try{$bitmap.Save($memory,[System.Drawing.Imaging.ImageFormat]::Png);$images.Add($memory.ToArray())}finally{$memory.Dispose()}}finally{$bitmap.Dispose()}
  }
  [System.IO.Directory]::CreateDirectory((Split-Path -Parent $DestinationIco))|Out-Null
  $stream=[System.IO.File]::Create($DestinationIco);$writer=[System.IO.BinaryWriter]::new($stream)
  try{$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count);$offset=6+(16*$sizes.Count);for($i=0;$i -lt $sizes.Count;$i++){$dimension=if($sizes[$i]-eq 256){0}else{$sizes[$i]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length};foreach($image in $images){$writer.Write($image)}}finally{$writer.Dispose();$stream.Dispose()}
}finally{$source.Dispose()}
Write-Output "ICO_OK $DestinationIco"
