# Build a multi-size .ico from whale-icon.png (ASCII only, no non-ASCII literals)
Add-Type -AssemblyName System.Drawing

$dir = "C:\Users\yachi\Documents\deepseek-harness\default-workspace\ds-web-launcher"
$png = Join-Path $dir "whale-icon.png"
$ico = Join-Path $dir "whale.ico"

$img = [System.Drawing.Image]::FromFile($png)
Write-Output ("source: {0}x{1} {2}" -f $img.Width, $img.Height, $img.PixelFormat)

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$data = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($img, 0, 0, $s, $s)
    $g.Dispose()

    $tmp = New-Object System.IO.MemoryStream
    $bmp.Save($tmp, [System.Drawing.Imaging.ImageFormat]::Png)
    $data += , $tmp.ToArray()
    $bmp.Dispose()
    $tmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)              # reserved
$bw.Write([UInt16]1)              # type = icon
$bw.Write([UInt16]$sizes.Count)   # count

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)         # width
    $bw.Write([Byte]$dim)         # height
    $bw.Write([Byte]0)            # color count
    $bw.Write([Byte]0)            # reserved
    $bw.Write([UInt16]1)          # planes
    $bw.Write([UInt16]32)         # bit count
    $bw.Write([UInt32]$data[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $data[$i].Length
}
foreach ($d in $data) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($ico, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
$img.Dispose()

Write-Output ("ico written: {0} bytes, {1} sizes: {2}" -f (Get-Item $ico).Length, $sizes.Count, ($sizes -join ','))

# read back and export previews for visual check
foreach ($checkSize in @(16, 32, 256)) {
    $ic = New-Object System.Drawing.Icon($ico, $checkSize, $checkSize)
    $b = $ic.ToBitmap()
    $preview = Join-Path $dir ("icon-preview-{0}.png" -f $checkSize)
    $b.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("  loaded {0} -> {1}x{2} saved {3}" -f $checkSize, $b.Width, $b.Height, (Split-Path $preview -Leaf))
    $b.Dispose()
    $ic.Dispose()
}
