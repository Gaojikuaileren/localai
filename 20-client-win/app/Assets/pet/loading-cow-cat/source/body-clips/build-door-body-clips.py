#!/usr/bin/env python3
"""Convert approved ImageGen door-action strips into strict 1x pixel source sheets."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image


CELL_SIZE = 128
FRAME_COUNT = 4
GROUND_Y = 112
PIVOT = [64, 112]
MAX_HALF_WIDTH = 60
MAX_HEIGHT = 58


def subject_mask(rgb: np.ndarray) -> np.ndarray:
    red = rgb[:, :, 0].astype(np.int16)
    green = rgb[:, :, 1].astype(np.int16)
    blue = rgb[:, :, 2].astype(np.int16)
    chroma_green = (
        (green >= 100)
        & (green >= red + 28)
        & (green >= blue + 28)
        & (green >= (red * 3) // 2)
        & (green >= (blue * 3) // 2)
    )
    return ~chroma_green


def quantized_rgba(rgb: np.ndarray, mask: np.ndarray) -> np.ndarray:
    luminance = (
        0.2126 * rgb[:, :, 0]
        + 0.7152 * rgb[:, :, 1]
        + 0.0722 * rgb[:, :, 2]
    )
    white = mask & (luminance >= 112)
    black = mask & ~white
    rgba = np.zeros((*mask.shape, 4), dtype=np.uint8)
    rgba[black] = [0, 0, 0, 255]
    rgba[white] = [255, 255, 255, 255]
    return rgba


def mask_bounds(mask: np.ndarray) -> tuple[int, int, int, int]:
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        raise ValueError("frame contains no subject pixels after chroma-key removal")
    return int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())


def ground_runs(mask: np.ndarray) -> list[list[int]]:
    xs = np.flatnonzero(mask[GROUND_Y])
    if len(xs) == 0:
        return []
    runs: list[list[int]] = []
    start = previous = int(xs[0])
    for raw_x in xs[1:]:
        x = int(raw_x)
        if x != previous + 1:
            runs.append([start, previous])
            start = x
        previous = x
    runs.append([start, previous])
    return runs


def extract_source_frames(path: Path) -> tuple[list[np.ndarray], list[tuple[int, int, int, int]], int, int]:
    image = Image.open(path).convert("RGB")
    if image.width % FRAME_COUNT != 0:
        raise ValueError(f"{path} width {image.width} is not divisible by {FRAME_COUNT}")
    source_cell_width = image.width // FRAME_COUNT
    source_frames: list[np.ndarray] = []
    bounds: list[tuple[int, int, int, int]] = []
    for index in range(FRAME_COUNT):
        crop = image.crop(
            (index * source_cell_width, 0, (index + 1) * source_cell_width, image.height)
        )
        rgb = np.asarray(crop, dtype=np.uint8)
        mask = subject_mask(rgb)
        bounds.append(mask_bounds(mask))
        source_frames.append(quantized_rgba(rgb, mask))
    return source_frames, bounds, source_cell_width, image.height


def choose_common_scale(
    all_bounds: list[tuple[int, int, int, int]],
    source_cell_width: int,
) -> float:
    center_x = (source_cell_width - 1) / 2.0
    max_half_extent = 1.0
    max_height = 1
    for min_x, min_y, max_x, max_y in all_bounds:
        max_half_extent = max(
            max_half_extent,
            center_x - min_x,
            max_x - center_x,
        )
        max_height = max(max_height, max_y - min_y + 1)
    return min(MAX_HALF_WIDTH / max_half_extent, MAX_HEIGHT / max_height)


def render_frame(
    source_rgba: np.ndarray,
    bounds: tuple[int, int, int, int],
    source_cell_width: int,
    scale: float,
) -> np.ndarray:
    min_x, min_y, max_x, max_y = bounds
    cut = Image.fromarray(source_rgba[min_y : max_y + 1, min_x : max_x + 1], mode="RGBA")
    output_width = max(1, int(round(cut.width * scale)))
    output_height = max(1, int(round(cut.height * scale)))
    resized = cut.resize((output_width, output_height), resample=Image.Resampling.NEAREST)

    source_center_x = (source_cell_width - 1) / 2.0
    output_x = int(round(64 + (min_x - source_center_x) * scale))
    output_y = GROUND_Y - output_height + 1
    if output_x < 0 or output_x + output_width > CELL_SIZE or output_y < 0:
        raise ValueError(
            f"scaled frame does not fit 128x128: x={output_x}, y={output_y}, "
            f"w={output_width}, h={output_height}"
        )

    frame = np.zeros((CELL_SIZE, CELL_SIZE, 4), dtype=np.uint8)
    resized_array = np.asarray(resized, dtype=np.uint8)
    alpha = resized_array[:, :, 3] == 255
    target = frame[
        output_y : output_y + output_height,
        output_x : output_x + output_width,
    ]
    target[alpha] = resized_array[alpha]
    return frame


def frame_stats(frame: np.ndarray, index: int) -> dict[str, object]:
    opaque = frame[:, :, 3] == 255
    white = opaque & np.all(frame[:, :, :3] == 255, axis=2)
    black = opaque & np.all(frame[:, :, :3] == 0, axis=2)
    min_x, min_y, max_x, max_y = mask_bounds(opaque)
    width = max_x - min_x + 1
    height = max_y - min_y + 1
    fx_anchor = [
        int(round(min_x + width * 0.18)),
        int(round(min_y + height * 0.22)),
    ]
    runs = ground_runs(opaque)
    contacts: list[dict[str, object]] = []
    if runs:
        contacts.append(
            {
                "part": "fore_near",
                "surface": "ground",
                "at": [int(round(sum(runs[0]) / 2)), GROUND_Y],
            }
        )
        if len(runs) > 1:
            contacts.append(
                {
                    "part": "hind_near",
                    "surface": "ground",
                    "at": [int(round(sum(runs[-1]) / 2)), GROUND_Y],
                }
            )
    return {
        "source_index": index,
        "bbox": [min_x, min_y, max_x, max_y],
        "opaque_count": int(opaque.sum()),
        "black_count": int(black.sum()),
        "white_count": int(white.sum()),
        "fx_anchor": fx_anchor,
        "contacts": contacts,
        "sha256_rgba": hashlib.sha256(frame.tobytes()).hexdigest(),
    }


def build_sheets(enter_source: Path, exit_source: Path, output_dir: Path) -> None:
    enter_frames, enter_bounds, enter_cell_width, _ = extract_source_frames(enter_source)
    exit_frames, exit_bounds, exit_cell_width, _ = extract_source_frames(exit_source)
    if enter_cell_width != exit_cell_width:
        raise ValueError("enter and exit source sheets use different cell widths")

    scale = choose_common_scale(enter_bounds + exit_bounds, enter_cell_width)
    output_dir.mkdir(parents=True, exist_ok=True)
    stats: dict[str, object] = {
        "format": "cowcat-door-body-source-stats@1",
        "cell": [CELL_SIZE, CELL_SIZE],
        "ground_y": GROUND_Y,
        "pivot": PIVOT,
        "common_scale": scale,
        "clips": {},
    }

    for clip_name, source_frames, bounds in (
        ("door_enter", enter_frames, enter_bounds),
        ("door_exit", exit_frames, exit_bounds),
    ):
        rendered = [
            render_frame(frame, frame_bounds, enter_cell_width, scale)
            for frame, frame_bounds in zip(source_frames, bounds, strict=True)
        ]
        sheet = np.concatenate(rendered, axis=1)
        sheet_path = output_dir / f"{clip_name}_left_1x.png"
        Image.fromarray(sheet, mode="RGBA").save(sheet_path, format="PNG", optimize=False)
        stats["clips"][clip_name] = {
            "file": sheet_path.name,
            "sheet_size": [CELL_SIZE * FRAME_COUNT, CELL_SIZE],
            "frames": [frame_stats(frame, index) for index, frame in enumerate(rendered)],
        }

    stats_path = output_dir / "door-body-clips-generated-stats.json"
    stats_path.write_text(
        json.dumps(stats, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--enter-source", type=Path, required=True)
    parser.add_argument("--exit-source", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, required=True)
    args = parser.parse_args()
    build_sheets(args.enter_source, args.exit_source, args.out_dir)


if __name__ == "__main__":
    main()
