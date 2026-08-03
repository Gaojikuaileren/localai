from __future__ import annotations

import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parent
SOURCE = ROOT.parent / "loading-cow-cat-icon-final-bw-polished-v1.png"
RESAMPLE = Image.Resampling.LANCZOS


def rounded_mask(size: tuple[int, int], radius: int, scale: int = 4) -> Image.Image:
    width, height = size
    mask = Image.new("L", (width * scale, height * scale), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        (0, 0, width * scale - 1, height * scale - 1),
        radius=radius * scale,
        fill=255,
    )
    return mask.resize(size, RESAMPLE)


source_l = Image.open(SOURCE).convert("L")
source_rgb = Image.merge("RGB", (source_l, source_l, source_l))
source_alpha = rounded_mask(source_l.size, radius=148)
master = Image.merge("RGBA", (*source_rgb.split(), source_alpha))

manifest: list[dict[str, object]] = []


def record(path: Path, purpose: str, width: int, height: int, variant: str) -> None:
    manifest.append(
        {
            "path": path.relative_to(ROOT).as_posix(),
            "purpose": purpose,
            "width": width,
            "height": height,
            "variant": variant,
        }
    )


def resize_icon(size: int) -> Image.Image:
    image = master.resize((size, size), RESAMPLE)
    if size <= 48:
        image = image.filter(ImageFilter.UnsharpMask(radius=0.45, percent=70, threshold=2))
    return image


def opaque(image: Image.Image, size: tuple[int, int] | None = None) -> Image.Image:
    target_size = size or image.size
    canvas = Image.new("RGB", target_size, "white")
    if image.size != target_size:
        image = image.resize(target_size, RESAMPLE)
    canvas.paste(image.convert("RGB"), (0, 0), image.getchannel("A"))
    return canvas


def save_png(path: Path, image: Image.Image, purpose: str, variant: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG", optimize=True)
    record(path, purpose, image.width, image.height, variant)


generic_sizes = [
    16,
    20,
    24,
    29,
    32,
    40,
    44,
    48,
    58,
    60,
    64,
    71,
    72,
    76,
    80,
    87,
    96,
    120,
    128,
    144,
    150,
    152,
    167,
    180,
    192,
    256,
    310,
    384,
    512,
    1024,
]

for size in generic_sizes:
    save_png(
        ROOT / "png" / f"icon-{size}x{size}.png",
        resize_icon(size),
        "generic square icon",
        "transparent rounded corners",
    )

# Browser and web-app assets.
for size in (16, 32, 48):
    save_png(
        ROOT / "web" / f"favicon-{size}x{size}.png",
        resize_icon(size),
        "browser favicon",
        "transparent rounded corners",
    )

favicon_path = ROOT / "web" / "favicon.ico"
master.save(favicon_path, format="ICO", sizes=[(16, 16), (32, 32), (48, 48)])
manifest.append(
    {
        "path": favicon_path.relative_to(ROOT).as_posix(),
        "purpose": "browser favicon bundle",
        "sizes": [16, 32, 48],
        "variant": "multi-frame ICO",
    }
)

apple_touch = opaque(resize_icon(180))
save_png(ROOT / "web" / "apple-touch-icon.png", apple_touch, "Apple Touch icon", "opaque white")

for size in (192, 512):
    pwa_icon = resize_icon(size)
    save_png(
        ROOT / "web" / f"pwa-icon-{size}x{size}.png",
        pwa_icon,
        "PWA any-purpose icon",
        "transparent rounded corners",
    )
    pwa_icon.save(ROOT / "web" / f"pwa-icon-{size}x{size}.webp", format="WEBP", lossless=True, method=6)
    record(
        ROOT / "web" / f"pwa-icon-{size}x{size}.webp",
        "PWA or web asset",
        size,
        size,
        "lossless WebP",
    )

    content_size = round(size * 0.80)
    content = resize_icon(content_size)
    maskable = Image.new("RGB", (size, size), "white")
    offset = ((size - content_size) // 2, (size - content_size) // 2)
    maskable.paste(content.convert("RGB"), offset, content.getchannel("A"))
    save_png(
        ROOT / "web" / f"pwa-maskable-{size}x{size}.png",
        maskable,
        "PWA maskable icon",
        "opaque with 10-percent safe margin",
    )

save_png(
    ROOT / "web" / "mstile-150x150.png",
    opaque(resize_icon(150)),
    "legacy Microsoft browser tile",
    "opaque white",
)

# Windows desktop and MSIX/UWP assets.
windows_ico = ROOT / "windows" / "loading-cow-cat.ico"
master.save(
    windows_ico,
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
manifest.append(
    {
        "path": windows_ico.relative_to(ROOT).as_posix(),
        "purpose": "Windows application and desktop shortcut",
        "sizes": [16, 20, 24, 32, 40, 48, 64, 128, 256],
        "variant": "multi-frame ICO",
    }
)

for filename, size in (
    ("Square44x44Logo.png", 44),
    ("Square71x71Logo.png", 71),
    ("Square150x150Logo.png", 150),
    ("Square310x310Logo.png", 310),
    ("app-icon-256x256.png", 256),
    ("app-icon-512x512.png", 512),
):
    save_png(
        ROOT / "windows" / filename,
        resize_icon(size),
        "Windows application asset",
        "transparent rounded corners",
    )

# Android density buckets and Play Store listing icon.
for density, size in (
    ("mdpi", 48),
    ("hdpi", 72),
    ("xhdpi", 96),
    ("xxhdpi", 144),
    ("xxxhdpi", 192),
):
    save_png(
        ROOT / "android" / f"icon-{density}-{size}x{size}.png",
        resize_icon(size),
        f"Android launcher {density}",
        "transparent rounded corners",
    )

save_png(
    ROOT / "android" / "play-store-512x512.png",
    opaque(resize_icon(512)),
    "Google Play listing",
    "opaque white",
)

# iOS/iPadOS point-size exports and App Store listing.
ios_assets = (
    ("icon-20@1x.png", 20),
    ("icon-20@2x.png", 40),
    ("icon-20@3x.png", 60),
    ("icon-29@1x.png", 29),
    ("icon-29@2x.png", 58),
    ("icon-29@3x.png", 87),
    ("icon-40@1x.png", 40),
    ("icon-40@2x.png", 80),
    ("icon-40@3x.png", 120),
    ("icon-60@2x.png", 120),
    ("icon-60@3x.png", 180),
    ("icon-76@1x.png", 76),
    ("icon-76@2x.png", 152),
    ("icon-83.5@2x.png", 167),
    ("app-store-1024x1024.png", 1024),
)

for filename, size in ios_assets:
    save_png(
        ROOT / "ios" / filename,
        opaque(resize_icon(size)),
        "iOS or iPadOS application icon",
        "opaque white",
    )

# Generic UI thumbnails and social previews.
for size in (64, 128, 256, 512):
    save_png(
        ROOT / "thumbnails" / f"thumbnail-{size}x{size}.png",
        opaque(resize_icon(size)),
        "UI thumbnail",
        "opaque white",
    )

save_png(
    ROOT / "thumbnails" / "social-square-1200x1200.png",
    opaque(resize_icon(1200)),
    "square social preview",
    "opaque white",
)

social_preview = Image.new("RGB", (1200, 630), "white")
social_icon = resize_icon(520)
social_preview.paste(social_icon.convert("RGB"), ((1200 - 520) // 2, (630 - 520) // 2), social_icon.getchannel("A"))
save_png(
    ROOT / "thumbnails" / "social-preview-1200x630.png",
    social_preview,
    "Open Graph or link preview",
    "opaque white with centered icon",
)

(ROOT / "manifest.json").write_text(
    json.dumps(
        {
            "source": SOURCE.name,
            "source_dimensions": list(source_l.size),
            "corner_policy": "transparent outside the source rounded square, except opaque maskable and platform listing assets",
            "assets": manifest,
        },
        ensure_ascii=False,
        indent=2,
    )
    + "\n",
    encoding="utf-8",
)

print(json.dumps({"generated": len(manifest), "root": str(ROOT)}, ensure_ascii=False))
