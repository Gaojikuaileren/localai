param(
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'loading-cow-cat-animation-manifest-v1.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$errors = [System.Collections.Generic.List[string]]::new()

function Add-ValidationError {
    param([string]$Message)
    $errors.Add($Message)
}

function Get-Sum {
    param([object[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) { return 0 }
    return [int](($Values | Measure-Object -Sum).Sum)
}

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Manifest not found: $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding utf8 | ConvertFrom-Json
$manifestDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $ManifestPath)
$clipEntries = @($manifest.clips.PSObject.Properties)
$clipNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

foreach ($entry in $clipEntries) {
    if (-not $clipNames.Add($entry.Name)) {
        Add-ValidationError "Duplicate clip id: $($entry.Name)"
    }
}

if ($manifest.format -ne 'cowcat-pixel-animation@1') {
    Add-ValidationError "Unexpected format: $($manifest.format)"
}
if ([int]$manifest.fps -ne 6) {
    Add-ValidationError "fps must be 6, got $($manifest.fps)"
}
if ([int]$manifest.canvas.width -ne 128 -or [int]$manifest.canvas.height -ne 128) {
    Add-ValidationError 'Canvas must be 128x128.'
}
if ($manifest.tracks.body.parallel -ne $false -or $manifest.tracks.body.full_body -ne $true) {
    Add-ValidationError 'body track must be non-parallel and full-body.'
}
if ($manifest.tracks.loading.parallel -ne $true -or $manifest.tracks.loading.full_body -ne $false) {
    Add-ValidationError 'loading track must be the parallel non-body track.'
}

$parallelLayers = @($manifest.parallel_layers)
if ($parallelLayers.Count -ne 1 -or $parallelLayers[0] -ne 'loading_spinner') {
    Add-ValidationError 'loading_spinner must be the only parallel layer.'
}

foreach ($entry in $clipEntries) {
    $name = $entry.Name
    $clip = $entry.Value
    $expectedDuration = [double]$clip.ticks * 1000.0 / [double]$manifest.fps
    $durationError = [math]::Abs([double]$clip.duration_ms - $expectedDuration)

    if ($durationError -gt 0.001) {
        Add-ValidationError "$name duration_ms differs from ticks/fps by $durationError ms."
    }
    if ([int]$clip.independent_frames -lt 1 -or [int]$clip.ticks -lt 1) {
        Add-ValidationError "$name must have positive independent_frames and ticks."
    }
    if ($name -eq 'loading_spinner') {
        if ($clip.track -ne 'loading' -or $clip.direction.from -ne 'none' -or $clip.direction.runtime_mirror -ne $false) {
            Add-ValidationError 'loading_spinner must use the loading track with no direction or mirror.'
        }
    }
    else {
        if ($clip.track -ne 'body') {
            Add-ValidationError "$name must use the body track."
        }
        if ($clip.direction.from -ne 'left' -or $clip.direction.runtime_mirror -ne $true) {
            Add-ValidationError "$name must be authored left-facing and support runtime mirror."
        }
        if ($name -eq 'turn_180') {
            if ($clip.direction.to -ne 'right') {
                Add-ValidationError 'turn_180 must be authored left-to-right.'
            }
        }
        elseif ($clip.direction.to -ne 'left') {
            Add-ValidationError "$name must remain left-facing."
        }
    }

    if ($clip.group -eq 'transition') {
        if ($clip.loop -ne $false -or $clip.must_finish -ne $true) {
            Add-ValidationError "$name is a transition and must be non-looping/must_finish."
        }
    }

    $frames = @($clip.frames)
    $clipStage = [string]$manifest.stage
    if ($null -ne $clip.PSObject.Properties['asset_stage']) {
        $clipStage = [string]$clip.asset_stage
    }
    if ($clipStage -eq 'production') {
        $distinctSprites = @($frames | ForEach-Object { $_.sprite } | Sort-Object -Unique)
        $holdTotal = Get-Sum @($frames | ForEach-Object { [int]$_.hold })

        if ($distinctSprites.Count -ne [int]$clip.independent_frames) {
            Add-ValidationError "$name declares $($clip.independent_frames) independent frames but has $($distinctSprites.Count) distinct sprites."
        }
        if ($holdTotal -ne [int]$clip.ticks) {
            Add-ValidationError "$name frame holds sum to $holdTotal, expected $($clip.ticks)."
        }
        if ($clip.group -eq 'locomotion') {
            foreach ($frame in $frames) {
                if ([int]$frame.hold -ne 1) {
                    Add-ValidationError "$name locomotion frame $($frame.sprite) must use hold=1."
                }
            }
        }

        if ($null -ne $clip.PSObject.Properties['source_sheet']) {
            $sourceSheetId = [string]$clip.source_sheet
            $sourceSheetProperty = $manifest.source_sheets.PSObject.Properties[$sourceSheetId]
            if ($null -eq $sourceSheetProperty) {
                Add-ValidationError "$name references missing source sheet $sourceSheetId."
            }
            else {
                $sourceSheet = $sourceSheetProperty.Value
                $sourcePath = Join-Path $manifestDirectory ([string]$sourceSheet.file)
                if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                    Add-ValidationError "$name source sheet is missing: $sourcePath"
                }
                if ((@($sourceSheet.cell) -join ',') -ne '128,128' -or (@($sourceSheet.sheet_size) -join ',') -ne '512,128') {
                    Add-ValidationError "$name source sheet must be four 128x128 cells in one 512x128 row."
                }
                if ([int]$sourceSheet.ground_y -ne 112 -or (@($sourceSheet.pivot) -join ',') -ne '64,112') {
                    Add-ValidationError "$name source sheet must share ground_y=112 and pivot=64,112."
                }
            }
        }

        if ($name -eq 'door_enter' -or $name -eq 'door_exit') {
            if ($frames.Count -ne 4 -or @($frames | Where-Object { [int]$_.hold -ne 1 }).Count -ne 0) {
                Add-ValidationError "$name must contain four one-tick frames."
            }
            $expectedEvent = if ($name -eq 'door_enter') { 'portal_enter' } else { 'portal_exit' }
            $eventCount = @($frames | Where-Object { $expectedEvent -in @($_.events) }).Count
            if ($eventCount -ne 1) {
                Add-ValidationError "$name must emit $expectedEvent exactly once."
            }
            for ($frameIndex = 0; $frameIndex -lt $frames.Count; $frameIndex++) {
                $frame = $frames[$frameIndex]
                if ([int]$frame.source_index -ne $frameIndex) {
                    Add-ValidationError "$name frame $frameIndex must use matching source_index."
                }
                if ([int]$frame.root_delta[0] -ge 0 -or [int]$frame.root_delta[1] -ne 0) {
                    Add-ValidationError "$name frame $frameIndex must move negative x and zero y in the authored-left master."
                }
                if ([int]$frame.door_anchor_offset[1] -ne 0) {
                    Add-ValidationError "$name frame $frameIndex must keep a zero-y door anchor offset."
                }
                if ($frameIndex -lt ($frames.Count - 1)) {
                    $nextFrame = $frames[$frameIndex + 1]
                    $resolvedNextX = [int]$frame.door_anchor_offset[0] + [int]$frame.root_delta[0]
                    if ($resolvedNextX -ne [int]$nextFrame.door_anchor_offset[0]) {
                        Add-ValidationError "$name frame $frameIndex root_delta does not reach the next door anchor offset."
                    }
                }
            }
            if (-not [bool]$frames[-1].can_exit -or @($frames[0..($frames.Count - 2)] | Where-Object { [bool]$_.can_exit }).Count -ne 0) {
                Add-ValidationError "$name must allow exit only on its final frame."
            }
            $visibleFractions = @($frames | ForEach-Object { [double]$_.visible_fraction })
            for ($frameIndex = 0; $frameIndex -lt ($visibleFractions.Count - 1); $frameIndex++) {
                if ($name -eq 'door_enter' -and $visibleFractions[$frameIndex] -le $visibleFractions[$frameIndex + 1]) {
                    Add-ValidationError 'door_enter visible_fraction must strictly decrease.'
                }
                if ($name -eq 'door_exit' -and $visibleFractions[$frameIndex] -ge $visibleFractions[$frameIndex + 1]) {
                    Add-ValidationError 'door_exit visible_fraction must strictly increase.'
                }
            }
        }
    }
}

$stateNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($stateEntry in @($manifest.states.PSObject.Properties)) {
    [void]$stateNames.Add($stateEntry.Name)
    $state = $stateEntry.Value
    if ($null -ne $state.PSObject.Properties['loop_clip']) {
        if (-not $clipNames.Contains([string]$state.loop_clip)) {
            Add-ValidationError "State $($stateEntry.Name) references missing loop clip $($state.loop_clip)."
        }
    }
    if ($null -ne $state.PSObject.Properties['insert_clips']) {
        foreach ($insertClip in @($state.insert_clips)) {
            if (-not $clipNames.Contains([string]$insertClip)) {
                Add-ValidationError "State $($stateEntry.Name) references missing insert clip $insertClip."
            }
        }
    }
}

foreach ($edge in @($manifest.edges)) {
    if (-not ([string]$edge.from).StartsWith('*') -and -not $stateNames.Contains([string]$edge.from)) {
        Add-ValidationError "Edge references missing from-state $($edge.from)."
    }
    if (-not $stateNames.Contains([string]$edge.to)) {
        Add-ValidationError "Edge references missing to-state $($edge.to)."
    }
    if ($null -ne $edge.clip) {
        $edgeClipName = [string]$edge.clip
        if (-not $clipNames.Contains($edgeClipName)) {
            Add-ValidationError "Edge $($edge.from)->$($edge.to) references missing clip $edgeClipName."
        }
        elseif ($edge.must_finish -eq $true) {
            $edgeClip = $manifest.clips.PSObject.Properties[$edgeClipName].Value
            if ($edgeClip.loop -ne $false -or $edgeClip.must_finish -ne $true) {
                Add-ValidationError "Edge clip $edgeClipName must be non-looping and must_finish."
            }
        }
    }
}

$bodyEntries = @($clipEntries | Where-Object { $_.Value.track -eq 'body' })
$loadingEntries = @($clipEntries | Where-Object { $_.Value.track -eq 'loading' })
$bodyFull = Get-Sum @($bodyEntries | ForEach-Object { [int]$_.Value.independent_frames })
$bodyV1a = Get-Sum @($bodyEntries | Where-Object { $_.Value.v1a -eq $true } | ForEach-Object { [int]$_.Value.independent_frames })
$loadingFull = Get-Sum @($loadingEntries | ForEach-Object { [int]$_.Value.independent_frames })
$loadingV1a = Get-Sum @($loadingEntries | Where-Object { $_.Value.v1a -eq $true } | ForEach-Object { [int]$_.Value.independent_frames })

$expectedCounts = @{
    bodyV1a = [int]$manifest.budgets.audited_v1a_body_frames
    bodyFull = [int]$manifest.budgets.audited_full_body_frames
    loadingV1a = [int]$manifest.budgets.v1a_loading_frames
    loadingFull = [int]$manifest.budgets.full_loading_frames
}
$actualCounts = @{
    bodyV1a = $bodyV1a
    bodyFull = $bodyFull
    loadingV1a = $loadingV1a
    loadingFull = $loadingFull
}

foreach ($key in $expectedCounts.Keys) {
    if ($actualCounts[$key] -ne $expectedCounts[$key]) {
        Add-ValidationError "$key is $($actualCounts[$key]), expected $($expectedCounts[$key])."
    }
}

$requiredFullEdges = @('loaf_to_sleep', 'sleep_to_loaf', 'stand_to_stalk', 'stalk_to_stand')
$edgeClipNames = @($manifest.edges | Where-Object { $null -ne $_.clip } | ForEach-Object { [string]$_.clip })
foreach ($requiredClip in $requiredFullEdges) {
    if ($requiredClip -notin $edgeClipNames) {
        Add-ValidationError "Full state graph is missing edge clip $requiredClip."
    }
}

$hasScratchCancel = @($manifest.edges | Where-Object {
    $_.from -eq 'scratch_door' -and $_.to -eq 'stand' -and $_.at_event -eq 'exit_seam'
}).Count -eq 1
if (-not $hasScratchCancel) {
    Add-ValidationError 'scratch_door must have one cancel edge to stand at exit_seam.'
}

if ($errors.Count -gt 0) {
    Write-Host "Animation manifest validation FAILED ($($errors.Count) errors):" -ForegroundColor Red
    foreach ($validationError in $errors) {
        Write-Host " - $validationError" -ForegroundColor Red
    }
    exit 1
}

Write-Host 'Animation manifest validation passed.' -ForegroundColor Green
Write-Host " stage:         $($manifest.stage)"
Write-Host " clips:         $($clipEntries.Count)"
Write-Host " v1a body:      $bodyV1a"
Write-Host " v1a loading:   $loadingV1a"
Write-Host " full body:     $bodyFull"
Write-Host " full loading:  $loadingFull"
