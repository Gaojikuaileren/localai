param(
    [string]$DoorDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$desktopPath = Join-Path $DoorDirectory 'door_desktop.png'
$corridorPath = Join-Path $DoorDirectory 'door_corridor.png'
$cellSize = 128
$groundY = 112
$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError {
    param([string]$Message)
    $errors.Add($Message)
}

function Get-PngHeader {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-ValidationError "Missing PNG: $Path"
        return $null
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 29) {
        Add-ValidationError "PNG is too short: $Path"
        return $null
    }

    $expectedSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $expectedSignature.Length; $i++) {
        if ($bytes[$i] -ne $expectedSignature[$i]) {
            Add-ValidationError "Invalid PNG signature: $Path"
            return $null
        }
    }

    $ihdrLength = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 8))
    $chunkType = [System.Text.Encoding]::ASCII.GetString($bytes, 12, 4)
    if ($ihdrLength -ne 13 -or $chunkType -ne 'IHDR') {
        Add-ValidationError "Invalid IHDR chunk in $Path"
        return $null
    }

    return [PSCustomObject]@{
        Width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        Height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
        BitDepth = [int]$bytes[24]
        ColorType = [int]$bytes[25]
        Compression = [int]$bytes[26]
        Filter = [int]$bytes[27]
        Interlace = [int]$bytes[28]
    }
}

function Test-PngHeader {
    param(
        [string]$Path,
        [int]$ExpectedWidth,
        [int]$ExpectedHeight
    )

    $header = Get-PngHeader $Path
    if ($null -eq $header) { return }

    if ($header.Width -ne $ExpectedWidth -or $header.Height -ne $ExpectedHeight) {
        Add-ValidationError "$(Split-Path $Path -Leaf) is $($header.Width)x$($header.Height), expected ${ExpectedWidth}x${ExpectedHeight}."
    }
    if ($header.BitDepth -ne 8 -or $header.ColorType -ne 6) {
        Add-ValidationError "$(Split-Path $Path -Leaf) must be source PNG 8-bit RGBA (color type 6)."
    }
    if ($header.Compression -ne 0 -or $header.Filter -ne 0 -or $header.Interlace -ne 0) {
        Add-ValidationError "$(Split-Path $Path -Leaf) must use standard compression/filter and no interlace."
    }
}

function Read-CellMask {
    param(
        [System.Drawing.Bitmap]$Bitmap,
        [int]$OffsetX,
        [string]$Label
    )

    $mask = [bool[]]::new($cellSize * $cellSize)
    $opaqueCount = 0
    $maxOpaqueY = -1

    for ($y = 0; $y -lt $cellSize; $y++) {
        for ($x = 0; $x -lt $cellSize; $x++) {
            $pixel = $Bitmap.GetPixel($OffsetX + $x, $y)
            if ($pixel.A -ne 0 -and $pixel.A -ne 255) {
                Add-ValidationError "$Label has non-binary alpha $($pixel.A) at ($x,$y)."
                continue
            }

            if ($pixel.A -eq 255) {
                if ($pixel.R -ne 255 -or $pixel.G -ne 255 -or $pixel.B -ne 255) {
                    Add-ValidationError "$Label has non-white opaque RGB at ($x,$y)."
                }
                if ($y -gt $groundY) {
                    Add-ValidationError "$Label has an opaque pixel below ground_y at ($x,$y)."
                }
                $mask[$y * $cellSize + $x] = $true
                $opaqueCount++
                if ($y -gt $maxOpaqueY) { $maxOpaqueY = $y }
            }
            elseif ($pixel.R -ne 0 -or $pixel.G -ne 0 -or $pixel.B -ne 0) {
                Add-ValidationError "$Label has hidden non-zero RGB at transparent pixel ($x,$y)."
            }
        }
    }

    if ($opaqueCount -eq 0) {
        Add-ValidationError "$Label is empty."
    }
    if ($maxOpaqueY -ne $groundY) {
        Add-ValidationError "$Label max opaque y is $maxOpaqueY, expected ground_y $groundY."
    }

    return [PSCustomObject]@{
        Label = $Label
        Mask = $mask
        OpaqueCount = $opaqueCount
        MaxOpaqueY = $maxOpaqueY
    }
}

function Get-JaccardDistance {
    param([bool[]]$A, [bool[]]$B)

    if ($A.Length -ne $B.Length) { throw 'Mask lengths differ.' }
    $union = 0
    $xor = 0
    for ($i = 0; $i -lt $A.Length; $i++) {
        if ($A[$i] -or $B[$i]) { $union++ }
        if ($A[$i] -ne $B[$i]) { $xor++ }
    }
    if ($union -eq 0) { return 0.0 }
    return [double]$xor / [double]$union
}

Test-PngHeader $desktopPath 512 128
Test-PngHeader $corridorPath 128 128

if ((Test-Path -LiteralPath $desktopPath) -and (Test-Path -LiteralPath $corridorPath)) {
    $desktopBitmap = [System.Drawing.Bitmap]::new($desktopPath)
    $corridorBitmap = [System.Drawing.Bitmap]::new($corridorPath)
    try {
        $desktopFrames = @()
        for ($frameIndex = 0; $frameIndex -lt 4; $frameIndex++) {
            $desktopFrames += Read-CellMask $desktopBitmap ($frameIndex * $cellSize) "door_desktop[$frameIndex]"
        }
        $corridor = Read-CellMask $corridorBitmap 0 'door_corridor[0]'

        for ($left = 0; $left -lt 4; $left++) {
            for ($right = $left + 1; $right -lt 4; $right++) {
                $distance = Get-JaccardDistance $desktopFrames[$left].Mask $desktopFrames[$right].Mask
                if ($distance -le 0.0) {
                    Add-ValidationError "door_desktop frames $left and $right are identical."
                }
            }
        }

        $d01 = Get-JaccardDistance $desktopFrames[0].Mask $desktopFrames[1].Mask
        $d02 = Get-JaccardDistance $desktopFrames[0].Mask $desktopFrames[2].Mask
        $d03 = Get-JaccardDistance $desktopFrames[0].Mask $desktopFrames[3].Mask
        $d32 = Get-JaccardDistance $desktopFrames[3].Mask $desktopFrames[2].Mask
        $d31 = Get-JaccardDistance $desktopFrames[3].Mask $desktopFrames[1].Mask
        $d30 = Get-JaccardDistance $desktopFrames[3].Mask $desktopFrames[0].Mask

        if ($d03 -lt 0.25) {
            Add-ValidationError "door_desktop first/last Jaccard distance is $d03, expected >= 0.25."
        }
        if (-not ($d01 -lt $d02 -and $d02 -lt $d03)) {
            Add-ValidationError "Opening order is not monotonic from the closed frame: $d01, $d02, $d03."
        }
        if (-not ($d32 -lt $d31 -and $d31 -lt $d30)) {
            Add-ValidationError "Opening order is not monotonic from the open frame: $d32, $d31, $d30."
        }

        $corridorDistances = @()
        for ($frameIndex = 0; $frameIndex -lt 4; $frameIndex++) {
            $distance = Get-JaccardDistance $corridor.Mask $desktopFrames[$frameIndex].Mask
            $corridorDistances += $distance
            if ($distance -lt 0.25) {
                Add-ValidationError "door_corridor is too similar to door_desktop[$frameIndex]: $distance."
            }
        }

        Write-Host 'Opaque pixels:'
        foreach ($frame in $desktopFrames) {
            Write-Host " $($frame.Label): $($frame.OpaqueCount), maxY=$($frame.MaxOpaqueY)"
        }
        Write-Host " $($corridor.Label): $($corridor.OpaqueCount), maxY=$($corridor.MaxOpaqueY)"
        Write-Host ('Desktop distances closed->open: {0:N4}, {1:N4}, {2:N4}' -f $d01, $d02, $d03)
        Write-Host ('Desktop distances open->closed: {0:N4}, {1:N4}, {2:N4}' -f $d32, $d31, $d30)
        Write-Host ('Corridor distances: {0}' -f (($corridorDistances | ForEach-Object { '{0:N4}' -f $_ }) -join ', '))
    }
    finally {
        $desktopBitmap.Dispose()
        $corridorBitmap.Dispose()
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Door mask validation FAILED ($($errors.Count) errors):" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host " - $validationError" -ForegroundColor Red
    }
    exit 1
}

$layerValidator = Join-Path $DoorDirectory 'validate-door-layers.ps1'
if (-not (Test-Path -LiteralPath $layerValidator -PathType Leaf)) {
    Write-Host "Missing layer validator: $layerValidator" -ForegroundColor Red
    exit 1
}
& $layerValidator -DoorDirectory $DoorDirectory

Write-Host 'Door mask validation passed.' -ForegroundColor Green
