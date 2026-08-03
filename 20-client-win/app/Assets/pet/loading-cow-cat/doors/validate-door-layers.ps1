param(
    [string]$DoorDirectory = $PSScriptRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$cellSize = 128
$groundY = 112
$errors = [System.Collections.Generic.List[string]]::new()

function Add-Error {
    param([string]$Message)
    $errors.Add($Message)
}

function Test-Header {
    param(
        [string]$Path,
        [int]$Width,
        [int]$Height
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Error "Missing layered PNG: $Path"
        return $false
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 29) {
        Add-Error "PNG is too short: $Path"
        return $false
    }

    $signature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    for ($i = 0; $i -lt $signature.Length; $i++) {
        if ($bytes[$i] -ne $signature[$i]) {
            Add-Error "Invalid PNG signature: $Path"
            return $false
        }
    }

    $actualWidth = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
    $actualHeight = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    if ($actualWidth -ne $Width -or $actualHeight -ne $Height) {
        Add-Error "$(Split-Path $Path -Leaf) is $actualWidth by $actualHeight, expected $Width by $Height."
    }
    if ([int]$bytes[24] -ne 8 -or [int]$bytes[25] -ne 6) {
        Add-Error "$(Split-Path $Path -Leaf) must be source 8-bit RGBA PNG."
    }
    if ([int]$bytes[28] -ne 0) {
        Add-Error "$(Split-Path $Path -Leaf) must not be interlaced."
    }
    return $true
}

function Read-MaskSheet {
    param(
        [string]$Path,
        [int]$FrameCount,
        [string]$Label,
        [int[]]$AllowEmptyFrames = @()
    )

    if (-not (Test-Header $Path ($FrameCount * $cellSize) $cellSize)) {
        return @()
    }

    $bitmap = [System.Drawing.Bitmap]::new($Path)
    try {
        $frames = @()
        for ($frameIndex = 0; $frameIndex -lt $FrameCount; $frameIndex++) {
            $mask = [bool[]]::new($cellSize * $cellSize)
            $opaqueCount = 0
            $maxY = -1
            for ($y = 0; $y -lt $cellSize; $y++) {
                for ($x = 0; $x -lt $cellSize; $x++) {
                    $pixel = $bitmap.GetPixel(($frameIndex * $cellSize) + $x, $y)
                    if ($pixel.A -ne 0 -and $pixel.A -ne 255) {
                        Add-Error "$Label[$frameIndex] has non-binary alpha $($pixel.A) at ($x,$y)."
                        continue
                    }
                    if ($pixel.A -eq 255) {
                        if ($pixel.R -ne 255 -or $pixel.G -ne 255 -or $pixel.B -ne 255) {
                            Add-Error "$Label[$frameIndex] has non-white opaque RGB at ($x,$y)."
                        }
                        if ($y -gt $groundY) {
                            Add-Error "$Label[$frameIndex] has opaque pixels below ground_y."
                        }
                        $mask[$y * $cellSize + $x] = $true
                        $opaqueCount++
                        if ($y -gt $maxY) { $maxY = $y }
                    }
                    elseif ($pixel.R -ne 0 -or $pixel.G -ne 0 -or $pixel.B -ne 0) {
                        Add-Error "$Label[$frameIndex] has hidden non-zero RGB at ($x,$y)."
                    }
                }
            }
            if ($opaqueCount -eq 0 -and $AllowEmptyFrames -notcontains $frameIndex) {
                Add-Error "$Label[$frameIndex] is empty."
            }
            $frames += [PSCustomObject]@{
                Label = "$Label[$frameIndex]"
                Mask = $mask
                Count = $opaqueCount
                MaxY = $maxY
            }
        }
        return $frames
    }
    finally {
        $bitmap.Dispose()
    }
}

function Get-Intersection {
    param([bool[]]$A, [bool[]]$B)

    $count = 0
    for ($i = 0; $i -lt $A.Length; $i++) {
        if ($A[$i] -and $B[$i]) { $count++ }
    }
    return $count
}

function Get-Xor {
    param([bool[]]$A, [bool[]]$B)

    $count = 0
    for ($i = 0; $i -lt $A.Length; $i++) {
        if ($A[$i] -ne $B[$i]) { $count++ }
    }
    return $count
}

function Get-Union {
    param([object[]]$Masks)

    $union = [bool[]]::new($cellSize * $cellSize)
    foreach ($mask in $Masks) {
        for ($i = 0; $i -lt $union.Length; $i++) {
            if ($mask[$i]) { $union[$i] = $true }
        }
    }
    return $union
}

function Get-Bounds {
    param([bool[]]$Mask)

    $minX = $cellSize
    $minY = $cellSize
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $cellSize; $y++) {
        for ($x = 0; $x -lt $cellSize; $x++) {
            if (-not $Mask[$y * $cellSize + $x]) { continue }
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
    if ($maxX -lt 0) { return $null }
    return @($minX, $minY, $maxX, $maxY)
}

function Get-Components4 {
    param([bool[]]$Mask)

    $visited = [bool[]]::new($Mask.Length)
    $count = 0
    for ($start = 0; $start -lt $Mask.Length; $start++) {
        if (-not $Mask[$start] -or $visited[$start]) { continue }
        $count++
        $queue = [System.Collections.Generic.Queue[int]]::new()
        $queue.Enqueue($start)
        $visited[$start] = $true

        while ($queue.Count -gt 0) {
            $index = $queue.Dequeue()
            $x = $index % $cellSize
            $y = [int]($index / $cellSize)
            $neighbors = @()
            if ($x -gt 0) { $neighbors += $index - 1 }
            if ($x -lt ($cellSize - 1)) { $neighbors += $index + 1 }
            if ($y -gt 0) { $neighbors += $index - $cellSize }
            if ($y -lt ($cellSize - 1)) { $neighbors += $index + $cellSize }
            foreach ($neighbor in $neighbors) {
                if ($Mask[$neighbor] -and -not $visited[$neighbor]) {
                    $visited[$neighbor] = $true
                    $queue.Enqueue($neighbor)
                }
            }
        }
    }
    return $count
}

function Assert-Disjoint {
    param($A, $B, [string]$Label)

    $overlap = Get-Intersection $A.Mask $B.Mask
    if ($overlap -ne 0) {
        Add-Error "$Label overlaps by $overlap pixels."
    }
}

function Assert-Portal {
    param(
        $Portal,
        $Back,
        $Front,
        [object[]]$ExpectedBounds,
        [int]$ExpectedArea,
        [string]$Label
    )

    Assert-Disjoint $Portal $Back "$Label/back"
    Assert-Disjoint $Portal $Front "$Label/front"

    if ($Portal.Count -ne $ExpectedArea) {
        Add-Error "$Label area is $($Portal.Count), expected $ExpectedArea."
    }
    $bounds = Get-Bounds $Portal.Mask
    if (($bounds -join ',') -ne ($ExpectedBounds -join ',')) {
        Add-Error "$Label bounds are $($bounds -join ','), expected $($ExpectedBounds -join ',')."
    }
    $components = Get-Components4 $Portal.Mask
    if ($components -ne 1) {
        Add-Error "$Label has $components four-connected components, expected one."
    }
}

$contractPath = Join-Path $DoorDirectory 'door-assets-v1.json'
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw "Missing contract: $contractPath"
}
$contract = Get-Content -Raw -Encoding UTF8 $contractPath | ConvertFrom-Json

$desktop = $contract.assets.door_desktop
$corridor = $contract.assets.door_corridor

$desktopComposite = Read-MaskSheet (Join-Path $DoorDirectory $desktop.file) 4 'desktop composite'
$desktopBack = (Read-MaskSheet (Join-Path $DoorDirectory $desktop.layers.back.file) 1 'desktop back')[0]
$desktopLeaf = Read-MaskSheet (Join-Path $DoorDirectory $desktop.layers.leaf.file) 4 'desktop leaf' @(3)
$desktopFront = (Read-MaskSheet (Join-Path $DoorDirectory $desktop.layers.front.file) 1 'desktop front')[0]
$desktopPortal = (Read-MaskSheet (Join-Path $DoorDirectory $desktop.layers.portal_mask.file) 1 'desktop portal')[0]

$corridorComposite = (Read-MaskSheet (Join-Path $DoorDirectory $corridor.file) 1 'corridor composite')[0]
$corridorBack = (Read-MaskSheet (Join-Path $DoorDirectory $corridor.layers.back.file) 1 'corridor back')[0]
$corridorFront = (Read-MaskSheet (Join-Path $DoorDirectory $corridor.layers.front.file) 1 'corridor front')[0]
$corridorPortal = (Read-MaskSheet (Join-Path $DoorDirectory $corridor.layers.portal_mask.file) 1 'corridor portal')[0]

if ($desktopComposite.Count -eq 4 -and $desktopLeaf.Count -eq 4) {
    Assert-Disjoint $desktopBack $desktopFront 'desktop back/front'
    $staticDoor = Get-Union @($desktopBack.Mask, $desktopFront.Mask)
    for ($frameIndex = 0; $frameIndex -lt 4; $frameIndex++) {
        Assert-Disjoint $desktopBack $desktopLeaf[$frameIndex] "desktop back/leaf[$frameIndex]"
        Assert-Disjoint $desktopFront $desktopLeaf[$frameIndex] "desktop front/leaf[$frameIndex]"
        $leafOutsidePortal = $desktopLeaf[$frameIndex].Count - (Get-Intersection $desktopLeaf[$frameIndex].Mask $desktopPortal.Mask)
        if ($leafOutsidePortal -ne 0) {
            Add-Error "desktop leaf[$frameIndex] has $leafOutsidePortal pixels outside the immutable portal."
        }
        $expected = Get-Union @($desktopBack.Mask, $desktopLeaf[$frameIndex].Mask, $desktopFront.Mask)
        $difference = Get-Xor $desktopComposite[$frameIndex].Mask $expected
        if ($difference -ne 0) {
            Add-Error "desktop composite[$frameIndex] differs from layer union by $difference pixels."
        }

        $staticDifference = 0
        for ($pixelIndex = 0; $pixelIndex -lt $staticDoor.Length; $pixelIndex++) {
            $actualStatic = $desktopComposite[$frameIndex].Mask[$pixelIndex] -and -not $desktopLeaf[$frameIndex].Mask[$pixelIndex]
            if ($actualStatic -ne $staticDoor[$pixelIndex]) { $staticDifference++ }
        }
        if ($staticDifference -ne 0) {
            Add-Error "desktop frame[$frameIndex] static remainder differs from back+front by $staticDifference pixels."
        }
    }

    for ($leftFrame = 0; $leftFrame -lt 4; $leftFrame++) {
        for ($rightFrame = $leftFrame + 1; $rightFrame -lt 4; $rightFrame++) {
            $deltaDifference = 0
            for ($pixelIndex = 0; $pixelIndex -lt $staticDoor.Length; $pixelIndex++) {
                $compositeDelta = $desktopComposite[$leftFrame].Mask[$pixelIndex] -ne $desktopComposite[$rightFrame].Mask[$pixelIndex]
                $leafDelta = $desktopLeaf[$leftFrame].Mask[$pixelIndex] -ne $desktopLeaf[$rightFrame].Mask[$pixelIndex]
                if ($compositeDelta -ne $leafDelta) { $deltaDifference++ }
            }
            if ($deltaDifference -ne 0) {
                Add-Error "desktop frames $leftFrame/$rightFrame change outside leaf by $deltaDifference pixels."
            }
        }
    }
}

Assert-Disjoint $corridorBack $corridorFront 'corridor back/front'
$expectedCorridor = Get-Union @($corridorBack.Mask, $corridorFront.Mask)
$corridorDifference = Get-Xor $corridorComposite.Mask $expectedCorridor
if ($corridorDifference -ne 0) {
    Add-Error "corridor composite differs from layer union by $corridorDifference pixels."
}

Assert-Portal $desktopPortal $desktopBack $desktopFront @($desktop.validation.portal_bbox) ([int]$desktop.validation.portal_area) 'desktop portal'
Assert-Portal $corridorPortal $corridorBack $corridorFront @($corridor.validation.portal_bbox) ([int]$corridor.validation.portal_area) 'corridor portal'

$desktopBounds = Get-Bounds $desktopPortal.Mask
$corridorBounds = Get-Bounds $corridorPortal.Mask
$desktopWidth = $desktopBounds[2] - $desktopBounds[0] + 1
$desktopHeight = $desktopBounds[3] - $desktopBounds[1] + 1
$corridorWidth = $corridorBounds[2] - $corridorBounds[0] + 1
$corridorHeight = $corridorBounds[3] - $corridorBounds[1] + 1

if ($desktopWidth -lt [int]$desktop.validation.portal_min_clearance[0] -or $desktopHeight -lt [int]$desktop.validation.portal_min_clearance[1]) {
    Add-Error "Desktop portal clearance is $desktopWidth by $desktopHeight, below contract minimum."
}
if ($corridorWidth -lt [int]$corridor.validation.portal_min_clearance[0] -or $corridorHeight -lt [int]$corridor.validation.portal_min_clearance[1]) {
    Add-Error "Corridor portal clearance is $corridorWidth by $corridorHeight, below contract minimum."
}
if ($desktop.validation.portal_ground_relation -ne 'ends_at_sill_top' -or $desktopBounds[3] -ne ([int]$desktop.validation.sill_top_y - 1)) {
    Add-Error 'Desktop portal must end exactly one pixel above the contracted sill.'
}
if ($corridor.validation.portal_ground_relation -ne 'ends_at_sill_top' -or $corridorBounds[3] -ne ([int]$corridor.validation.sill_top_y - 1)) {
    Add-Error 'Corridor portal must end exactly one pixel above the contracted sill.'
}

$corridorAspect = [double]$corridorWidth / [double]$corridorHeight
if ($corridorAspect -lt [double]$corridor.validation.aspect_ratio_range[0] -or $corridorAspect -gt [double]$corridor.validation.aspect_ratio_range[1]) {
    Add-Error "Corridor portal aspect ratio is $corridorAspect, outside the contracted range."
}

$symmetryMismatch = 0
for ($y = $corridorBounds[1]; $y -le $corridorBounds[3]; $y++) {
    for ($x = $corridorBounds[0]; $x -le $corridorBounds[2]; $x++) {
        $mirrorX = $corridorBounds[0] + $corridorBounds[2] - $x
        if ($corridorPortal.Mask[$y * $cellSize + $x] -ne $corridorPortal.Mask[$y * $cellSize + $mirrorX]) {
            $symmetryMismatch++
        }
    }
}
$symmetryRatio = [double]$symmetryMismatch / [double]$corridorPortal.Count
if ($symmetryRatio -gt [double]$corridor.validation.symmetry_tolerance) {
    Add-Error "Corridor portal horizontal symmetry error is $symmetryRatio."
}

$rowLeftEdges = @()
$rowRightEdges = @()
$rowWidths = @()
for ($y = $corridorBounds[1]; $y -le $corridorBounds[3]; $y++) {
    $runCount = 0
    $rowWidth = 0
    $rowLeft = $cellSize
    $rowRight = -1
    $insideRun = $false
    for ($x = $corridorBounds[0]; $x -le $corridorBounds[2]; $x++) {
        $opaque = $corridorPortal.Mask[$y * $cellSize + $x]
        if ($opaque) {
            $rowWidth++
            if ($x -lt $rowLeft) { $rowLeft = $x }
            if ($x -gt $rowRight) { $rowRight = $x }
            if (-not $insideRun) {
                $runCount++
                $insideRun = $true
            }
        }
        else {
            $insideRun = $false
        }
    }
    if ($runCount -ne 1) {
        Add-Error "Corridor portal row y=$y has $runCount runs, expected one."
    }
    $rowLeftEdges += $rowLeft
    $rowRightEdges += $rowRight
    $rowWidths += $rowWidth
}

$maxPortalRowWidth = [int](($rowWidths | Measure-Object -Maximum).Maximum)
if ($rowWidths[0] -ge $maxPortalRowWidth -or $rowWidths[-1] -ge $maxPortalRowWidth) {
    Add-Error 'Soft cat-cave portal must be pinched at both the top and lower lip.'
}

$maxStraightEdgeRun = 0
foreach ($edgeValues in @($rowLeftEdges, $rowRightEdges)) {
    $currentRun = 0
    $previousValue = $null
    foreach ($edgeValue in $edgeValues) {
        if ($null -ne $previousValue -and $edgeValue -eq $previousValue) {
            $currentRun++
        }
        else {
            $currentRun = 1
            $previousValue = $edgeValue
        }
        if ($currentRun -gt $maxStraightEdgeRun) { $maxStraightEdgeRun = $currentRun }
    }
}
if ($maxStraightEdgeRun -gt [int]$corridor.validation.portal_max_straight_edge_run) {
    Add-Error "Soft cat-cave portal has a $maxStraightEdgeRun px straight jamb run."
}

$outerBounds = Get-Bounds $corridorComposite.Mask
$frontBounds = Get-Bounds $corridorFront.Mask
if (($outerBounds -join ',') -ne (@($corridor.validation.soft_body_bbox) -join ',')) {
    Add-Error "Corridor soft body bbox is $($outerBounds -join ',')."
}
if (($frontBounds -join ',') -ne (@($corridor.validation.front_lip_bbox) -join ',')) {
    Add-Error "Corridor front lip bbox is $($frontBounds -join ',')."
}
if (-not (
    $outerBounds[0] -lt $frontBounds[0] -and
    $outerBounds[1] -lt $frontBounds[1] -and
    $outerBounds[2] -gt $frontBounds[2] -and
    $outerBounds[3] -ge $frontBounds[3] -and
    $frontBounds[0] -lt $corridorBounds[0] -and
    $frontBounds[1] -lt $corridorBounds[1] -and
    $frontBounds[2] -gt $corridorBounds[2] -and
    $frontBounds[3] -gt $corridorBounds[3]
)) {
    Add-Error 'Corridor plush shell must surround both the near lip and portal.'
}

$outerWidth = $outerBounds[2] - $outerBounds[0] + 1
$outerHeight = $outerBounds[3] - $outerBounds[1] + 1
if (([double]$outerWidth / [double]$outerHeight) -lt [double]$corridor.validation.outer_width_height_ratio_min) {
    Add-Error 'Corridor plush shell is too tall and arch-like.'
}

$topRowRuns = 0
$insideTopRun = $false
for ($x = $outerBounds[0]; $x -le $outerBounds[2]; $x++) {
    $opaque = $corridorComposite.Mask[$outerBounds[1] * $cellSize + $x]
    if ($opaque -and -not $insideTopRun) {
        $topRowRuns++
        $insideTopRun = $true
    }
    elseif (-not $opaque) {
        $insideTopRun = $false
    }
}
$topFoldNotches = [Math]::Max(0, $topRowRuns - 1)
if ($topFoldNotches -lt [int]$corridor.validation.top_fold_notch_count_min) {
    Add-Error 'Corridor plush shell is missing the open top fold that distinguishes it from a hard arch.'
}

if ((Get-Components4 $corridorBack.Mask) -ne 1) {
    Add-Error 'Corridor plush back must remain one four-connected shape.'
}
if ((Get-Components4 $corridorFront.Mask) -ne 1) {
    Add-Error 'Corridor plush front lip must remain one four-connected shape.'
}

$expectedDesktopAnchor = @([int](($desktopBounds[0] + $desktopBounds[2]) / 2), $groundY)
$expectedCorridorAnchor = @([int](($corridorBounds[0] + $corridorBounds[2]) / 2), $groundY)
if ($desktop.interaction.anchor.position_ref -ne 'portal.bottom_center' -or (@($desktop.interaction.anchor.resolved) -join ',') -ne ($expectedDesktopAnchor -join ',')) {
    Add-Error 'Desktop interaction anchor is not derived from the portal bottom center.'
}
if ($corridor.interaction.anchor.position_ref -ne 'portal.bottom_center' -or (@($corridor.interaction.anchor.resolved) -join ',') -ne ($expectedCorridorAnchor -join ',')) {
    Add-Error 'Corridor interaction anchor is not derived from the portal bottom center.'
}
if ($desktop.interaction.occlusion_plane.position_ref -ne 'portal.max_x' -or [int]$desktop.interaction.occlusion_plane.resolved_position -ne $desktopBounds[2]) {
    Add-Error 'Desktop occlusion plane must resolve to portal.max_x.'
}
if ($corridor.interaction.occlusion_plane.position_ref -ne 'portal.min_x' -or [int]$corridor.interaction.occlusion_plane.resolved_position -ne $corridorBounds[0]) {
    Add-Error 'Corridor occlusion plane must resolve to portal.min_x.'
}

$coverages = @()
for ($frameIndex = 0; $frameIndex -lt 4; $frameIndex++) {
    $overlap = Get-Intersection $desktopPortal.Mask $desktopLeaf[$frameIndex].Mask
    $coverages += [double]$overlap / [double]$desktopPortal.Count
}

$coverageRules = $desktop.validation.leaf_portal_coverage
if ($coverages[0] -lt [double]$coverageRules.closed_min) {
    Add-Error "Closed desktop leaf coverage is $($coverages[0])."
}
if ($coverages[3] -gt [double]$coverageRules.fully_open_max) {
    Add-Error "Fully-open desktop leaf coverage is $($coverages[3])."
}
for ($frameIndex = 0; $frameIndex -lt 3; $frameIndex++) {
    $drop = $coverages[$frameIndex] - $coverages[$frameIndex + 1]
    if ($drop -lt [double]$coverageRules.min_step_drop) {
        Add-Error "Desktop leaf coverage drop $frameIndex to $($frameIndex + 1) is only $drop."
    }
}

if ((@($desktop.open_sequence) -join ',') -ne '0,1,2,3') {
    Add-Error 'Desktop open_sequence must be 0,1,2,3.'
}
if ((@($desktop.close_sequence) -join ',') -ne '3,2,1,0') {
    Add-Error 'Desktop close_sequence must be 3,2,1,0.'
}
if ((@($contract.layer_contract.clip_targets) -join ',') -ne 'cat,loading') {
    Add-Error 'Portal clip targets must include cat and loading in that order.'
}
if ([bool]$contract.layer_contract.portal_masks_render) {
    Add-Error 'Portal masks must not render.'
}
if ((@($contract.layer_contract.profiles.cat_front_with_leaf.draw_order) -join ',') -ne 'back,leaf,cat,front') {
    Add-Error 'Outside desktop draw order must be back,leaf,cat,front.'
}
if ((@($contract.layer_contract.profiles.cat_behind_with_leaf.draw_order) -join ',') -ne 'back,cat,leaf,front') {
    Add-Error 'Behind desktop draw order must be back,cat,leaf,front.'
}
if ((@($desktop.motion_contract.animated_layers) -join ',') -ne 'leaf' -or (@($desktop.motion_contract.static_layers) -join ',') -ne 'back,front,portal_mask') {
    Add-Error 'Desktop motion contract must animate only leaf and keep back/front/portal static.'
}
if ($desktop.motion_contract.portal_frame_binding -ne 'constant_for_entire_clip') {
    Add-Error 'Desktop portal must remain immutable for the entire clip.'
}
if ((@($corridor.motion_contract.animated_layers) -join ',') -ne '' -or (@($corridor.motion_contract.static_layers) -join ',') -ne 'back,front,portal_mask') {
    Add-Error 'Corridor must have no animated door layers.'
}
if ($corridor.motion_contract.portal_frame_binding -ne 'constant_for_entire_clip' -or $null -ne $corridor.layers.leaf) {
    Add-Error 'Corridor portal must be immutable and have no leaf.'
}
if ($corridor.semantic_id -ne 'door_other_client' -or $corridor.destination_kind -ne 'other_client' -or $corridor.link_scope -ne 'cross_client' -or $corridor.geometry.topology -ne 'soft_cat_cave_mouth') {
    Add-Error 'Corridor must be a soft cat-cave cross-client doorway, not an arch or resting place.'
}

Write-Host ('Desktop leaf portal coverage: {0}' -f (($coverages | ForEach-Object { '{0:N4}' -f $_ }) -join ', '))
Write-Host "Desktop portal: area=$($desktopPortal.Count), bbox=$((Get-Bounds $desktopPortal.Mask) -join ',')"
Write-Host "Corridor portal: area=$($corridorPortal.Count), bbox=$((Get-Bounds $corridorPortal.Mask) -join ',')"

if ($errors.Count -gt 0) {
    Write-Host "Door layer validation FAILED ($($errors.Count) errors):" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host " - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host 'Door layer validation passed.' -ForegroundColor Green
