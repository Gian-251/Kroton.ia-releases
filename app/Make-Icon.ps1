# Gera o icone multirresolucao (kroton.ico) e o logo 64x64 (kroton-logo.png) a partir de um PNG.
# Uso: powershell -ExecutionPolicy Bypass -File app\Make-Icon.ps1
#      powershell -ExecutionPolicy Bypass -File app\Make-Icon.ps1 -Source ..\outro.png
param(
    [string]$SourcePath = 'd:\Kroton.ia\app\kroton.png',
    [string]$IcoPath = 'd:\Kroton.ia\app\kroton.ico',
    [string]$LogoPath = 'd:\Kroton.ia\app\kroton-logo.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $SourcePath)) { throw "Imagem de origem nao encontrada: $SourcePath" }

$source = [System.Drawing.Bitmap]::FromFile($SourcePath)

function New-ScaledBitmap {
    param([System.Drawing.Bitmap]$Image, [int]$Size)
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($Image, (New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)))
    $g.Dispose()
    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bmp)
    $w = $Bmp.Width
    $h = $Bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $Bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (altura dobrada: XOR + AND mask)
    $bw.Write([int]40)
    $bw.Write([int]$w)
    $bw.Write([int]($h * 2))
    $bw.Write([int16]1)
    $bw.Write([int16]32)
    $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    # XOR (BGRA, bottom-up)
    for ($y = $h - 1; $y -ge 0; $y--) {
        $bw.Write($pixels, ($y * $stride), ($w * 4))
    }
    # AND mask (1bpp, linhas alinhadas a 4 bytes) - tudo zero (alpha manda)
    $maskRowBytes = [int]([Math]::Ceiling($w / 32.0)) * 4
    $mask = New-Object byte[] ($maskRowBytes * $h)
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    return $ms.ToArray()
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bmp)
    $ms = New-Object System.IO.MemoryStream
    $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# 16..64 -> DIB (compatibilidade maxima); 128/256 -> PNG (padrao Vista+)
$plan = @(
    @{ Size = 16;  Png = $false },
    @{ Size = 24;  Png = $false },
    @{ Size = 32;  Png = $false },
    @{ Size = 48;  Png = $false },
    @{ Size = 64;  Png = $false },
    @{ Size = 128; Png = $false },
    @{ Size = 256; Png = $true }
)

$images = @()
foreach ($item in $plan) {
    $bmp = New-ScaledBitmap -Image $source -Size $item.Size
    $bytes = if ($item.Png) { Get-PngBytes -Bmp $bmp } else { Get-DibBytes -Bmp $bmp }
    $images += [PSCustomObject]@{ Size = $item.Size; Bytes = $bytes }
    $bmp.Dispose()
}

# Monta o arquivo ICO
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([int16]0)                        # reserved
$bw.Write([int16]1)                        # type = icon
$bw.Write([int16]$images.Count)            # count

$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $bw.Write([byte]$dim)                  # width
    $bw.Write([byte]$dim)                  # height
    $bw.Write([byte]0)                     # colors
    $bw.Write([byte]0)                     # reserved
    $bw.Write([int16]1)                    # planes
    $bw.Write([int16]32)                   # bpp
    $bw.Write([int]$img.Bytes.Length)      # size
    $bw.Write([int]$offset)                # offset
    $offset += $img.Bytes.Length
}
foreach ($img in $images) {
    $bw.Write($img.Bytes, 0, $img.Bytes.Length)
}
$bw.Flush()
[IO.File]::WriteAllBytes($icoPath, $ms.ToArray())

# Logo PNG para embutir na GUI (64x64)
$logoBmp = New-ScaledBitmap -Image $source -Size 64
$logoBmp.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
$logoBmp.Dispose()
$source.Dispose()

Write-Host ('ICO gerado: ' + (Get-Item $icoPath).Length + ' bytes (' + $images.Count + ' tamanhos)')
Write-Host ('LOGO gerado: ' + (Get-Item $logoPath).Length + ' bytes')
