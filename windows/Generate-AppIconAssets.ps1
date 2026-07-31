[CmdletBinding()]
param(
    [ValidateRange(0.75, 0.96)]
    [double]$VisibleHeightRatio = 0.90
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$assetDirectory = Join-Path $repositoryRoot 'src\TwitchAI.App\Assets'
$masterPath = Join-Path $assetDirectory 'TwitchAI.IconMaster.png'
$icoPath = Join-Path $assetDirectory 'TwitchAI.ico'

if (-not (Test-Path -LiteralPath $masterPath -PathType Leaf)) {
    throw "Das Icon-Master wurde nicht gefunden: $masterPath"
}

function Get-VisibleBounds {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $minimumX = $Bitmap.Width
    $minimumY = $Bitmap.Height
    $maximumX = -1
    $maximumY = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -eq 0) {
                continue
            }

            $minimumX = [Math]::Min($minimumX, $x)
            $minimumY = [Math]::Min($minimumY, $y)
            $maximumX = [Math]::Max($maximumX, $x)
            $maximumY = [Math]::Max($maximumY, $y)
        }
    }

    if ($maximumX -lt 0 -or $maximumY -lt 0) {
        throw 'Das Icon-Master enthält keine sichtbaren Pixel.'
    }

    return [System.Drawing.Rectangle]::FromLTRB(
        $minimumX,
        $minimumY,
        $maximumX + 1,
        $maximumY + 1)
}

function New-RenderedIcon {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Source,

        [Parameter(Mandatory)]
        [System.Drawing.Rectangle]$SourceBounds,

        [Parameter(Mandatory)]
        [ValidateRange(1, 256)]
        [int]$Size
    )

    $targetHeight = [Math]::Max(
        1,
        [int][Math]::Round(
            $Size * $VisibleHeightRatio,
            [MidpointRounding]::AwayFromZero))
    $targetWidth = [Math]::Max(
        1,
        [int][Math]::Round(
            $SourceBounds.Width * $targetHeight / $SourceBounds.Height,
            [MidpointRounding]::AwayFromZero))

    if ($targetWidth -gt $Size) {
        $targetWidth = $Size
        $targetHeight = [Math]::Max(
            1,
            [int][Math]::Round(
                $SourceBounds.Height * $targetWidth / $SourceBounds.Width,
                [MidpointRounding]::AwayFromZero))
    }

    $destinationX = [int][Math]::Floor(($Size - $targetWidth) / 2.0)
    $destinationY = [int][Math]::Floor(($Size - $targetHeight) / 2.0)
    $destination = [System.Drawing.Rectangle]::new(
        $destinationX,
        $destinationY,
        $targetWidth,
        $targetHeight)

    $canvas = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode =
            [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality =
            [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode =
            [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode =
            [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode =
            [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.DrawImage(
            $Source,
            $destination,
            $SourceBounds,
            [System.Drawing.GraphicsUnit]::Pixel)
    }
    finally {
        $graphics.Dispose()
    }

    return $canvas
}

function ConvertTo-PngBytes {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$Bitmap
    )

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return [byte[]]$stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-IcoFile {
    param(
        [Parameter(Mandatory)]
        [object[]]$Frames,

        [Parameter(Mandatory)]
        [string]$Path
    )

    $stream = [System.IO.FileStream]::new(
        $Path,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Frames.Count)

        $dataOffset = 6 + (16 * $Frames.Count)
        foreach ($frame in $Frames) {
            $dimension = if ($frame.Size -eq 256) {
                [byte]0
            }
            else {
                [byte]$frame.Size
            }

            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$dataOffset)
            $dataOffset += $frame.Bytes.Length
        }

        foreach ($frame in $Frames) {
            $writer.Write([byte[]]$frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$master = [System.Drawing.Bitmap]::new($masterPath)
try {
    $visibleBounds = Get-VisibleBounds -Bitmap $master

    # Exact-size frames prevent Windows from shrinking one oversized bitmap
    # differently between Explorer, the taskbar, Alt+Tab, and the title bar.
    $icoSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)
    $icoFrames = foreach ($size in $icoSizes) {
        $rendered = New-RenderedIcon `
            -Source $master `
            -SourceBounds $visibleBounds `
            -Size $size
        try {
            [pscustomobject]@{
                Size = $size
                Bytes = ConvertTo-PngBytes -Bitmap $rendered
            }
        }
        finally {
            $rendered.Dispose()
        }
    }
    Write-IcoFile -Frames $icoFrames -Path $icoPath

    # The unpackaged EXE uses the ICO. Packaged Windows surfaces prefer these
    # target-size, unplated PNG variants when an exact physical size exists.
    $targetSizes = @(16, 20, 24, 30, 32, 36, 40, 44, 48, 60, 64, 72, 80, 96, 256)
    foreach ($size in $targetSizes) {
        $rendered = New-RenderedIcon `
            -Source $master `
            -SourceBounds $visibleBounds `
            -Size $size
        try {
            $targetPath = Join-Path $assetDirectory (
                "Square44x44Logo.targetsize-{0}_altform-unplated.png" -f $size)
            $rendered.Save(
                $targetPath,
                [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $rendered.Dispose()
        }
    }

    # This is the 200-percent scale asset and is also used in the custom
    # WinUI title bar.
    $scaled44 = New-RenderedIcon `
        -Source $master `
        -SourceBounds $visibleBounds `
        -Size 88
    try {
        $scaled44.Save(
            (Join-Path $assetDirectory 'Square44x44Logo.scale-200.png'),
            [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $scaled44.Dispose()
    }

    Write-Host 'Windows-App-Icons wurden erfolgreich erzeugt.' -ForegroundColor Green
    Write-Host ("Sichtbare Motivhöhe: {0:P0}" -f $VisibleHeightRatio)
    Write-Host "ICO: $icoPath"
}
finally {
    $master.Dispose()
}
