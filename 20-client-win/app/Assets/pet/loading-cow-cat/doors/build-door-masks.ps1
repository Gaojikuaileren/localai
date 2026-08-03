param(
    [string]$OutputDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$cellSize = 128
$groundY = 112
$pixelFormat = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
$white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
$transparent = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(0, 0, 0, 0))

function New-MaskBitmap {
    param([int]$Width, [int]$Height)

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, $pixelFormat)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::None
        $graphics.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
    }
    finally {
        $graphics.Dispose()
    }
    return $bitmap
}

function New-MaskGraphics {
    param([System.Drawing.Bitmap]$Bitmap)

    $graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::None
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::None
    return $graphics
}

function Fill-InclusiveRectangle {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [int]$X1,
        [int]$Y1,
        [int]$X2,
        [int]$Y2
    )

    if ($X2 -lt $X1 -or $Y2 -lt $Y1) {
        throw "Invalid inclusive rectangle: ($X1,$Y1)-($X2,$Y2)"
    }
    $Graphics.FillRectangle($Brush, $X1, $Y1, ($X2 - $X1 + 1), ($Y2 - $Y1 + 1))
}

function Fill-DesktopShellShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # A compact pet-flap housing mounted low on a home door; never human-door scale.
    Fill-InclusiveRectangle $Graphics $Brush 48 37 127 42
    Fill-InclusiveRectangle $Graphics $Brush 51 43 127 49
    Fill-InclusiveRectangle $Graphics $Brush 48 50 54 108
    Fill-InclusiveRectangle $Graphics $Brush 124 50 127 108
    Fill-InclusiveRectangle $Graphics $Brush 50 109 127 $groundY
}

function Fill-DesktopPortalShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # Large fixed aperture sized around the unchanged full-scale cat.
    Fill-InclusiveRectangle $Graphics $Brush 61 46 117 49
    Fill-InclusiveRectangle $Graphics $Brush 55 50 123 108
}

function Fill-DesktopFrontShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # Near-side jamb and sill render over the cat to sell the crossing depth.
    Fill-InclusiveRectangle $Graphics $Brush 48 37 127 40
    Fill-InclusiveRectangle $Graphics $Brush 51 43 60 49
    Fill-InclusiveRectangle $Graphics $Brush 118 43 127 49
    Fill-InclusiveRectangle $Graphics $Brush 51 50 54 108
    Fill-InclusiveRectangle $Graphics $Brush 50 109 127 $groundY
}

function Fill-DesktopBack {
    param([System.Drawing.Graphics]$Graphics)

    Fill-DesktopShellShape $Graphics $white
    Fill-DesktopPortalShape $Graphics $transparent
    Fill-DesktopFrontShape $Graphics $transparent
}

function Fill-DesktopFront {
    param([System.Drawing.Graphics]$Graphics)
    Fill-DesktopFrontShape $Graphics $white
}

function Fill-DesktopPortal {
    param([System.Drawing.Graphics]$Graphics)
    Fill-DesktopPortalShape $Graphics $white
}

function Fill-DesktopLeaf {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$FrameIndex
    )

    # The top-hinged leaf always stays inside the portal mask. It swings inward,
    # so the normal outside profile draws it behind the cat.
    switch ($FrameIndex) {
        0 {
            Fill-DesktopPortalShape $Graphics $white
        }
        1 {
            Fill-InclusiveRectangle $Graphics $white 61 46 117 49
            Fill-InclusiveRectangle $Graphics $white 59 50 119 66
            Fill-InclusiveRectangle $Graphics $white 60 67 118 87
            Fill-InclusiveRectangle $Graphics $white 61 88 117 96
        }
        2 {
            Fill-InclusiveRectangle $Graphics $white 61 46 117 49
            Fill-InclusiveRectangle $Graphics $white 59 50 119 53
            Fill-InclusiveRectangle $Graphics $white 61 54 117 63
            Fill-InclusiveRectangle $Graphics $white 64 64 114 72
        }
        3 {
            # Fully open: the leaf has rotated out of the front projection.
        }
        default {
            throw "Desktop leaf frame index must be 0..3, got $FrameIndex"
        }
    }
}

function Fill-DesktopComposite {
    param(
        [System.Drawing.Graphics]$Graphics,
        [int]$FrameIndex
    )

    # Explicit OR-by-drawing. Do not DrawImage transparent layers with SourceCopy.
    Fill-DesktopBack $Graphics
    Fill-DesktopLeaf $Graphics $FrameIndex
    Fill-DesktopFront $Graphics
}

function Fill-CorridorShellShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # A squashed, irregular plush mass rather than an architectural arch.
    # Alternating side bulges and pinches are deliberate binary-pixel cues for
    # stuffed fabric; the opening remains large enough for the unchanged cat.
    Fill-InclusiveRectangle $Graphics $Brush 24 30 88 33
    Fill-InclusiveRectangle $Graphics $Brush 12 34 101 37
    Fill-InclusiveRectangle $Graphics $Brush 6 38 106 41
    Fill-InclusiveRectangle $Graphics $Brush 2 42 110 45
    Fill-InclusiveRectangle $Graphics $Brush 1 46 111 49
    Fill-InclusiveRectangle $Graphics $Brush 1 50 106 55
    Fill-InclusiveRectangle $Graphics $Brush 0 56 109 61
    Fill-InclusiveRectangle $Graphics $Brush 2 62 110 67
    Fill-InclusiveRectangle $Graphics $Brush 0 68 108 75
    Fill-InclusiveRectangle $Graphics $Brush 1 76 111 83
    Fill-InclusiveRectangle $Graphics $Brush 0 84 109 91
    Fill-InclusiveRectangle $Graphics $Brush 3 92 106 99
    Fill-InclusiveRectangle $Graphics $Brush 6 100 103 108
    Fill-InclusiveRectangle $Graphics $Brush 11 109 98 $groundY
}

function Fill-CorridorPortalShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # The inner mouth is a low, soft-cornered cave opening with a short,
    # slightly off-centre top. Avoid the semicircle + straight jambs language
    # of a built arch.
    Fill-InclusiveRectangle $Graphics $Brush 27 47 70 50
    Fill-InclusiveRectangle $Graphics $Brush 21 51 78 54
    Fill-InclusiveRectangle $Graphics $Brush 18 55 82 59
    Fill-InclusiveRectangle $Graphics $Brush 16 60 84 68
    Fill-InclusiveRectangle $Graphics $Brush 17 69 83 78
    Fill-InclusiveRectangle $Graphics $Brush 16 79 84 88
    Fill-InclusiveRectangle $Graphics $Brush 18 89 82 98
    Fill-InclusiveRectangle $Graphics $Brush 19 99 81 108
}

function Cut-CorridorBackFolds {
    param([System.Drawing.Graphics]$Graphics)

    # Open-ended notches read as compressed fabric folds at true 1x. They do
    # not add a colour or a second portal, and they reconnect inside the shell.
    Fill-InclusiveRectangle $Graphics $transparent 56 30 62 35
    Fill-InclusiveRectangle $Graphics $transparent 58 36 61 39
    Fill-InclusiveRectangle $Graphics $transparent 60 40 61 42
    Fill-InclusiveRectangle $Graphics $transparent 0 62 7 64
    Fill-InclusiveRectangle $Graphics $transparent 0 65 3 67
}

function Fill-CorridorFrontShape {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush
    )

    # Near-side stuffed rim: a top-right droop flows into two side bulges.
    # Its changing thickness and stepped lip carry the soft-material read even
    # when runtime tinting reduces the resource to a single flat colour.
    Fill-InclusiveRectangle $Graphics $Brush 66 42 85 45
    Fill-InclusiveRectangle $Graphics $Brush 72 46 97 49
    Fill-InclusiveRectangle $Graphics $Brush 79 50 101 54
    Fill-InclusiveRectangle $Graphics $Brush 83 55 105 59
    Fill-InclusiveRectangle $Graphics $Brush 85 60 108 69
    Fill-InclusiveRectangle $Graphics $Brush 86 70 109 78
    Fill-InclusiveRectangle $Graphics $Brush 85 79 110 88
    Fill-InclusiveRectangle $Graphics $Brush 83 89 107 98
    Fill-InclusiveRectangle $Graphics $Brush 82 99 103 108
    # Small left fold and a compressed foreground lip; neither becomes a bed.
    Fill-InclusiveRectangle $Graphics $Brush 4 89 17 94
    Fill-InclusiveRectangle $Graphics $Brush 5 95 17 101
    Fill-InclusiveRectangle $Graphics $Brush 9 102 18 108
    Fill-InclusiveRectangle $Graphics $Brush 18 109 82 $groundY
}

function Fill-CorridorBack {
    param([System.Drawing.Graphics]$Graphics)

    Fill-CorridorShellShape $Graphics $white
    Cut-CorridorBackFolds $Graphics
    Fill-CorridorPortalShape $Graphics $transparent
    Fill-CorridorFrontShape $Graphics $transparent
    # Remove sub-pixel-looking remnants where the near lip pinches against the
    # shell. The back layer must remain one readable plush mass at true 1x.
    Fill-InclusiveRectangle $Graphics $transparent 82 69 127 $groundY
    Fill-InclusiveRectangle $Graphics $transparent 0 99 18 $groundY
}

function Fill-CorridorFront {
    param([System.Drawing.Graphics]$Graphics)
    Fill-CorridorFrontShape $Graphics $white
}

function Fill-CorridorPortal {
    param([System.Drawing.Graphics]$Graphics)
    Fill-CorridorPortalShape $Graphics $white
}

function Fill-CorridorComposite {
    param([System.Drawing.Graphics]$Graphics)

    Fill-CorridorBack $Graphics
    Fill-CorridorFront $Graphics
}

function Save-StaticMask {
    param(
        [string]$Path,
        [scriptblock]$Draw
    )

    $bitmap = New-MaskBitmap $cellSize $cellSize
    try {
        $graphics = New-MaskGraphics $bitmap
        try {
            & $Draw $graphics
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Save-FrameSheet {
    param(
        [string]$Path,
        [int]$FrameCount,
        [scriptblock]$DrawFrame
    )

    $sheet = New-MaskBitmap ($cellSize * $FrameCount) $cellSize
    try {
        $sheetGraphics = New-MaskGraphics $sheet
        try {
            for ($frameIndex = 0; $frameIndex -lt $FrameCount; $frameIndex++) {
                $frame = New-MaskBitmap $cellSize $cellSize
                try {
                    $frameGraphics = New-MaskGraphics $frame
                    try {
                        & $DrawFrame $frameGraphics $frameIndex
                    }
                    finally {
                        $frameGraphics.Dispose()
                    }
                    $sheetGraphics.DrawImageUnscaled($frame, ($frameIndex * $cellSize), 0)
                }
                finally {
                    $frame.Dispose()
                }
            }
        }
        finally {
            $sheetGraphics.Dispose()
        }
        $sheet.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $sheet.Dispose()
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$paths = [ordered]@{
    DesktopComposite = Join-Path $OutputDirectory 'door_desktop.png'
    DesktopBack = Join-Path $OutputDirectory 'door_desktop_back.png'
    DesktopFront = Join-Path $OutputDirectory 'door_desktop_front.png'
    DesktopLeaf = Join-Path $OutputDirectory 'door_desktop_leaf.png'
    DesktopPortal = Join-Path $OutputDirectory 'door_desktop_portal_mask.png'
    CorridorComposite = Join-Path $OutputDirectory 'door_corridor.png'
    CorridorBack = Join-Path $OutputDirectory 'door_corridor_back.png'
    CorridorFront = Join-Path $OutputDirectory 'door_corridor_front.png'
    CorridorPortal = Join-Path $OutputDirectory 'door_corridor_portal_mask.png'
}

try {
    Save-FrameSheet $paths.DesktopComposite 4 { param($graphics, $index) Fill-DesktopComposite $graphics $index }
    Save-StaticMask $paths.DesktopBack { param($graphics) Fill-DesktopBack $graphics }
    Save-StaticMask $paths.DesktopFront { param($graphics) Fill-DesktopFront $graphics }
    Save-FrameSheet $paths.DesktopLeaf 4 { param($graphics, $index) Fill-DesktopLeaf $graphics $index }
    Save-StaticMask $paths.DesktopPortal { param($graphics) Fill-DesktopPortal $graphics }

    Save-StaticMask $paths.CorridorComposite { param($graphics) Fill-CorridorComposite $graphics }
    Save-StaticMask $paths.CorridorBack { param($graphics) Fill-CorridorBack $graphics }
    Save-StaticMask $paths.CorridorFront { param($graphics) Fill-CorridorFront $graphics }
    Save-StaticMask $paths.CorridorPortal { param($graphics) Fill-CorridorPortal $graphics }
}
finally {
    $white.Dispose()
    $transparent.Dispose()
}

foreach ($path in $paths.Values) {
    Write-Host "Wrote $path"
}
