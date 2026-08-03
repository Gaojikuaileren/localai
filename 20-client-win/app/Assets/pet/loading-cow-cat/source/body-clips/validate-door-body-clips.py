#!/usr/bin/env python3
"""Validate and preview the four-frame cat-through-door source sheets."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from typing import Iterable

from PIL import Image


CELL = 128
GROUND_Y = 112
ALLOWED_RGBA = {
    (0, 0, 0, 0),
    (0, 0, 0, 255),
    (255, 255, 255, 255),
}


def rgba_image(path: Path) -> Image.Image:
    image = Image.open(path)
    if image.mode != "RGBA":
        raise ValueError(f"{path.name}: source PNG mode is {image.mode}, expected RGBA")
    return image


def alpha_points(image: Image.Image, x_offset: int = 0) -> set[tuple[int, int]]:
    return {
        (x - x_offset, y)
        for y in range(image.height)
        for x in range(x_offset, image.width)
        if image.getpixel((x, y))[3] == 255
    }


def frame_points(sheet: Image.Image, index: int) -> set[tuple[int, int]]:
    return {
        (x, y)
        for y in range(CELL)
        for x in range(CELL)
        if sheet.getpixel((index * CELL + x, y))[3] == 255
    }


def frame_rgba_bytes(sheet: Image.Image, index: int) -> bytes:
    return sheet.crop((index * CELL, 0, (index + 1) * CELL, CELL)).tobytes()


def bounds(points: set[tuple[int, int]]) -> list[int]:
    return [
        min(x for x, _ in points),
        min(y for _, y in points),
        max(x for x, _ in points),
        max(y for _, y in points),
    ]


def shifted_runtime_points(
    points: Iterable[tuple[int, int]],
    anchor: list[int],
    offset: list[int],
    pivot: list[int],
    runtime_mirror: bool,
) -> set[tuple[int, int]]:
    if runtime_mirror:
        return {
            (
                anchor[0] - offset[0] - (x - pivot[0]),
                anchor[1] + offset[1] + (y - pivot[1]),
            )
            for x, y in points
        }
    return {
        (
            anchor[0] + offset[0] + (x - pivot[0]),
            anchor[1] + offset[1] + (y - pivot[1]),
        )
        for x, y in points
    }


def mask_points(path: Path, frame_index: int = 0) -> set[tuple[int, int]]:
    image = rgba_image(path)
    if image.width % CELL != 0 or image.height != CELL:
        raise ValueError(f"{path.name}: mask sheet must use 128x128 cells")
    return frame_points(image, frame_index)


def validate_sheet(path: Path, source: dict, frames: list[dict], stats: dict) -> list[set[tuple[int, int]]]:
    image = rgba_image(path)
    expected_size = tuple(source["sheet_size"])
    if image.size != expected_size:
        raise ValueError(f"{path.name}: size {image.size}, expected {expected_size}")

    illegal = sorted(set(image.get_flattened_data()) - ALLOWED_RGBA)
    if illegal:
        raise ValueError(f"{path.name}: contains illegal RGBA values, first={illegal[0]}")

    result: list[set[tuple[int, int]]] = []
    for index, frame in enumerate(frames):
        points = frame_points(image, index)
        if not points:
            raise ValueError(f"{path.name}[{index}]: empty frame")
        if min(x for x, _ in points) < 0 or max(x for x, _ in points) >= CELL:
            raise ValueError(f"{path.name}[{index}]: cat is cut by a horizontal cell edge")
        if max(y for _, y in points) != GROUND_Y:
            raise ValueError(f"{path.name}[{index}]: must touch ground_y={GROUND_Y}")
        if any(y > GROUND_Y for _, y in points):
            raise ValueError(f"{path.name}[{index}]: contains pixels below ground_y")
        box = bounds(points)
        if box[2] - box[0] + 1 < 90 or box[3] - box[1] + 1 < 35:
            raise ValueError(f"{path.name}[{index}]: silhouette is too small to be a complete cat")
        if len(points) < 2000:
            raise ValueError(f"{path.name}[{index}]: silhouette has too few pixels for a complete cat")

        expected_stats = stats["frames"][index]
        actual_hash = hashlib.sha256(frame_rgba_bytes(image, index)).hexdigest()
        if actual_hash != expected_stats["sha256_rgba"]:
            raise ValueError(f"{path.name}[{index}]: RGBA hash differs from generated stats")
        if box != expected_stats["bbox"] or len(points) != expected_stats["opaque_count"]:
            raise ValueError(f"{path.name}[{index}]: geometry differs from generated stats")

        if frame["source_index"] != index:
            raise ValueError(f"{path.name}[{index}]: source_index must equal cell index")
        if frame["pivot"] != source["pivot"]:
            raise ValueError(f"{path.name}[{index}]: pivot differs from source-sheet pivot")
        for contact in frame["contacts"]:
            x, y = contact["at"]
            if y != GROUND_Y or (x, y) not in points:
                raise ValueError(f"{path.name}[{index}]: contact {contact['at']} is not on an opaque ground pixel")
        result.append(points)
    return result


def draw_mask(canvas: Image.Image, points: Iterable[tuple[int, int]], origin_x: int, color: tuple[int, int, int]) -> None:
    for x, y in points:
        target_x = origin_x + x
        if 0 <= target_x < canvas.width and 0 <= y < canvas.height:
            canvas.putpixel((target_x, y), color)


def draw_cat(
    canvas: Image.Image,
    sheet: Image.Image,
    index: int,
    world_points: set[tuple[int, int]],
    visible_points: set[tuple[int, int]],
    anchor: list[int],
    offset: list[int],
    pivot: list[int],
    runtime_mirror: bool,
    origin_x: int,
) -> None:
    for local_y in range(CELL):
        for local_x in range(CELL):
            rgba = sheet.getpixel((index * CELL + local_x, local_y))
            if rgba[3] == 0:
                continue
            if runtime_mirror:
                world_x = anchor[0] - offset[0] - (local_x - pivot[0])
            else:
                world_x = anchor[0] + offset[0] + (local_x - pivot[0])
            world_y = anchor[1] + offset[1] + (local_y - pivot[1])
            point = (world_x, world_y)
            if point not in world_points or point not in visible_points:
                continue
            target_x = origin_x + world_x
            if 0 <= target_x < canvas.width and 0 <= world_y < canvas.height:
                canvas.putpixel((target_x, world_y), rgba[:3])


def checker_canvas(width: int, height: int) -> Image.Image:
    canvas = Image.new("RGB", (width, height), (212, 212, 212))
    pixels = canvas.load()
    for y in range(height):
        for x in range(width):
            if ((x // 8) + (y // 8)) % 2:
                pixels[x, y] = (190, 190, 190)
    for x in range(width):
        pixels[x, GROUND_Y + 1] = (126, 126, 126)
    return canvas


def render_preview_frame(
    pet_dir: Path,
    sheet: Image.Image,
    frame_index: int,
    frame: dict,
    door: dict,
    binding: dict,
    world_points: set[tuple[int, int]],
    visible_points: set[tuple[int, int]],
) -> Image.Image:
    width = 320
    door_origin_x = 80
    canvas = checker_canvas(width, CELL)
    back = mask_points(pet_dir / "doors" / door["layers"]["back"]["file"])
    front = mask_points(pet_dir / "doors" / door["layers"]["front"]["file"])
    draw_mask(canvas, back, door_origin_x, (137, 116, 104))

    leaf_layer = door["layers"].get("leaf")
    if leaf_layer is not None and "leaf_frame" in binding:
        leaf = mask_points(
            pet_dir / "doors" / leaf_layer["file"],
            int(binding["leaf_frame"]),
        )
        draw_mask(canvas, leaf, door_origin_x, (118, 99, 90))

    draw_cat(
        canvas,
        sheet,
        frame_index,
        world_points,
        visible_points,
        list(door["interaction"]["anchor"]["resolved"]),
        frame["door_anchor_offset"],
        frame["pivot"],
        bool(binding["runtime_mirror"]),
        door_origin_x,
    )
    draw_mask(canvas, front, door_origin_x, (166, 142, 126))
    return canvas.resize((width * 3, CELL * 3), Image.Resampling.NEAREST)


def validate_and_preview(pet_dir: Path, preview_dir: Path | None) -> None:
    manifest = json.loads((pet_dir / "loading-cow-cat-animation-manifest-v1.json").read_text(encoding="utf-8"))
    contract = json.loads((pet_dir / "doors" / "door-assets-v1.json").read_text(encoding="utf-8"))
    stats = json.loads((pet_dir / "source" / "body-clips" / "door-body-clips-generated-stats.json").read_text(encoding="utf-8"))

    rendered_previews: dict[str, list[Image.Image]] = {}
    for clip_name in ("door_enter", "door_exit"):
        clip = manifest["clips"][clip_name]
        binding = contract["clip_bindings"][clip_name]
        source = manifest["source_sheets"][clip["source_sheet"]]
        sheet_path = pet_dir / source["file"]
        clip_stats = stats["clips"][clip_name]
        frames = clip["frames"]
        if clip.get("asset_stage") != "production":
            raise ValueError(f"{clip_name}: completed source sheet must be asset_stage=production")
        if len(frames) != 4 or sum(frame["hold"] for frame in frames) != 4:
            raise ValueError(f"{clip_name}: must contain four one-tick frames")
        if clip["source_sheet"] != binding["source_sheet_ref"]:
            raise ValueError(f"{clip_name}: source-sheet binding mismatch")

        cat_frames = validate_sheet(sheet_path, source, frames, clip_stats)
        sheet = rgba_image(sheet_path)
        door = contract["assets"][binding["door_asset"]]
        portal = mask_points(pet_dir / "doors" / door["layers"]["portal_mask"]["file"])
        front = mask_points(pet_dir / "doors" / door["layers"]["front"]["file"])
        anchor = list(door["interaction"]["anchor"]["resolved"])
        profiles = binding["profiles_by_source_frame"]
        visible_values: list[float] = []
        front_overlap_total = 0
        previews: list[Image.Image] = []

        for index, (frame, cat_points, expected_profile) in enumerate(zip(frames, cat_frames, profiles, strict=True)):
            if frame["render_profile"] != expected_profile:
                raise ValueError(f"{clip_name}[{index}]: render profile differs from door binding")
            world = shifted_runtime_points(
                cat_points,
                anchor,
                frame["door_anchor_offset"],
                frame["pivot"],
                bool(binding["runtime_mirror"]),
            )
            if expected_profile == "behind":
                visible = (world & portal) - front
            else:
                visible = world - front
            actual_fraction = len(visible) / len(cat_points)
            if abs(actual_fraction - float(frame["visible_fraction"])) > 0.000001:
                raise ValueError(
                    f"{clip_name}[{index}]: visible_fraction {frame['visible_fraction']} "
                    f"does not match measured {actual_fraction:.6f}"
                )
            visible_values.append(actual_fraction)
            front_overlap_total += len(world & front)
            previews.append(
                render_preview_frame(
                    pet_dir,
                    sheet,
                    index,
                    frame,
                    door,
                    binding,
                    world,
                    visible,
                )
            )

        for index in range(3):
            current = frames[index]
            following = frames[index + 1]
            expected_offset = [
                current["door_anchor_offset"][0] + current["root_delta"][0],
                current["door_anchor_offset"][1] + current["root_delta"][1],
            ]
            if expected_offset != following["door_anchor_offset"]:
                raise ValueError(f"{clip_name}[{index}]: root_delta does not reach the next anchor offset")
        terminal = [
            frames[-1]["door_anchor_offset"][0] + frames[-1]["root_delta"][0],
            frames[-1]["door_anchor_offset"][1] + frames[-1]["root_delta"][1],
        ]
        if terminal != binding["terminal_anchor_offset_master"]:
            raise ValueError(f"{clip_name}: final root_delta does not reach the contracted terminal offset")
        terminal_world = shifted_runtime_points(
            cat_frames[-1],
            anchor,
            terminal,
            frames[-1]["pivot"],
            bool(binding["runtime_mirror"]),
        )
        if terminal_world & portal or terminal_world & front:
            raise ValueError(f"{clip_name}: actor has not fully cleared the portal and near lip after the final tick")
        if any(frame["root_delta"][0] >= 0 or frame["root_delta"][1] != 0 for frame in frames):
            raise ValueError(f"{clip_name}: authored-left root deltas must be negative-x and zero-y")
        if front_overlap_total == 0:
            raise ValueError(f"{clip_name}: near-side layer never occludes the actor")

        event_name = binding["profile_switch_event"]
        event_count = sum(event_name in frame["events"] for frame in frames)
        if event_count != 1:
            raise ValueError(f"{clip_name}: {event_name} must occur exactly once")
        if not frames[-1]["can_exit"] or any(frame["can_exit"] for frame in frames[:-1]):
            raise ValueError(f"{clip_name}: only the final frame may allow exit")
        if clip_name == "door_enter":
            if not all(a > b for a, b in zip(visible_values, visible_values[1:])):
                raise ValueError("door_enter visible fraction must strictly decrease")
        else:
            if not all(a < b for a, b in zip(visible_values, visible_values[1:])):
                raise ValueError("door_exit visible fraction must strictly increase")
        rendered_previews[clip_name] = previews

    if preview_dir is not None:
        preview_dir.mkdir(parents=True, exist_ok=True)
        for clip_name, frames in rendered_previews.items():
            strip = Image.new("RGB", (frames[0].width * 4, frames[0].height), (96, 96, 96))
            for index, frame in enumerate(frames):
                strip.paste(frame, (index * frame.width, 0))
            strip.save(preview_dir / f"{clip_name}-runtime-preview-strip.png")
            gif_frames = frames + [frames[-1]] * 2
            gif_frames[0].save(
                preview_dir / f"{clip_name}-runtime-preview.gif",
                save_all=True,
                append_images=gif_frames[1:],
                duration=[167, 167, 167, 167, 333, 333],
                loop=0,
                disposal=2,
            )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--pet-dir",
        type=Path,
        default=Path(__file__).resolve().parents[2],
    )
    parser.add_argument("--preview-dir", type=Path)
    args = parser.parse_args()
    validate_and_preview(args.pet_dir.resolve(), args.preview_dir.resolve() if args.preview_dir else None)
    print("Door body clip validation passed.")


if __name__ == "__main__":
    main()
