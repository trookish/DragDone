"""Composes the Microsoft Store listing screenshots from the app's own renders.

The Store wants at least one desktop screenshot at 1366x768 or larger; the README captures are
smaller than that. Every image here uses a render the app produced itself (`--snapshot`, `--show`),
placed on a neutral 1920x1080 canvas with a short caption. Nothing is invented: if a feature is not
in the app, it is not in the list.

    python packaging/make_store_screenshots.py [--out packaging/StoreScreenshots] [--size 1920x1080]
"""
from __future__ import annotations

import argparse
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
IMAGES = os.path.join(ROOT, "docs", "images")

FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_SEMI = r"C:\Windows\Fonts\seguisb.ttf"
FONT_TEXT = r"C:\Windows\Fonts\segoeui.ttf"

INK = (243, 246, 248)
DIM = (156, 168, 178)
BACKDROP_TOP = (22, 26, 30)
BACKDROP_BOTTOM = (9, 11, 13)


def font(path: str, size: int) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(path, size)


def accent_from(path: str) -> tuple[int, int, int]:
    """The render's own accent colour, so the caption matches the screenshot exactly."""
    with Image.open(path) as im:
        raw = im.convert("RGB").resize((160, 160)).tobytes()
    best, best_score = (43, 212, 196), -1
    for i in range(0, len(raw), 3):
        r, g, b = raw[i], raw[i + 1], raw[i + 2]
        hi, lo = max(r, g, b), min(r, g, b)
        score = (hi - lo) * 2 + hi
        if score > best_score and hi > 60:
            best, best_score = (r, g, b), score
    return best


def backdrop(size: tuple[int, int], glow: tuple[int, int, int]) -> Image.Image:
    w, h = size
    base = Image.new("RGB", size)
    draw = ImageDraw.Draw(base)
    for y in range(h):
        t = y / max(h - 1, 1)
        draw.line(
            [(0, y), (w, y)],
            fill=tuple(int(BACKDROP_TOP[i] + (BACKDROP_BOTTOM[i] - BACKDROP_TOP[i]) * t) for i in range(3)),
        )
    halo = Image.new("RGB", size, (0, 0, 0))
    hdraw = ImageDraw.Draw(halo)
    cx, cy = int(w * 0.68), int(h * 0.5)
    radius = int(min(w, h) * 0.46)
    for step in range(radius, 0, -8):
        t = 1 - step / radius
        colour = tuple(int(glow[i] * 0.10 * t) for i in range(3))
        hdraw.ellipse([cx - step, cy - step, cx + step, cy + step], fill=colour)
    halo = halo.filter(ImageFilter.GaussianBlur(60))
    return Image.blend(base, Image.blend(base, halo, 1.0).point(lambda v: v), 0.0) if False else _add(base, halo)


def _add(base: Image.Image, halo: Image.Image) -> Image.Image:
    from PIL import ImageChops

    return ImageChops.add(base, halo)


def paste_render(canvas: Image.Image, path: str, area: tuple[int, int, int, int],
                 margin: int = 64, shadow: int = 48) -> None:
    """Fits a render inside area (x, y, w, h), centres it vertically and gives it a real edge.

    The captures are client areas, so their own window frame is missing; rounding the corners and
    drawing a hairline border makes the bottom edge read as a window rather than a cut image.
    """
    x, y, max_w, max_h = area
    with Image.open(path) as src:
        im = src.convert("RGBA")
    scale = min(max_w / im.width, (max_h - 2 * margin) / im.height, 1.35)
    im = im.resize((max(1, int(im.width * scale)), max(1, int(im.height * scale))), Image.LANCZOS)

    radius = max(10, im.width // 60)
    mask = Image.new("L", im.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, im.width - 1, im.height - 1], radius=radius, fill=255)
    alpha = Image.composite(im.split()[3], Image.new("L", im.size, 0), mask)
    im.putalpha(alpha)

    edge = Image.new("RGBA", im.size, (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(
        [0, 0, im.width - 1, im.height - 1], radius=radius, outline=(255, 255, 255, 26), width=2)
    im = Image.alpha_composite(im, edge)

    top = y + (max_h - im.height) // 2
    shade = Image.new("RGBA", (im.width + 2 * shadow, im.height + 2 * shadow), (0, 0, 0, 0))
    shade.paste(Image.new("RGBA", im.size, (0, 0, 0, 235)), (shadow, shadow), alpha)
    shade = shade.filter(ImageFilter.GaussianBlur(shadow // 2))
    canvas.paste(Image.new("RGBA", shade.size, (0, 0, 0, 255)), (x - shadow // 2, top - shadow // 2), shade)
    canvas.paste(im, (x, top), im)


def wrap(draw: ImageDraw.ImageDraw, text: str, f: ImageFont.FreeTypeFont, width: int) -> list[str]:
    words, lines, current = text.split(), [], ""
    for word in words:
        trail = f"{current} {word}".strip()
        if draw.textlength(trail, font=f) <= width:
            current = trail
        else:
            if current:
                lines.append(current)
            current = word
    if current:
        lines.append(current)
    return lines


def caption(canvas: Image.Image, x: int, top: int, width: int, height: int, accent,
            title: str, sub: str, bullets: list[str], tight: bool = False) -> None:
    """Draws the text block, vertically centred in the area it was given."""
    draw = ImageDraw.Draw(canvas)
    f_title = font(FONT_BOLD, 62 if tight else 66)
    f_sub = font(FONT_TEXT, 30)
    f_bul = font(FONT_TEXT, 28)

    title_lines = wrap(draw, title, f_title, width)
    sub_lines = wrap(draw, sub, f_sub, width)
    bullet_lines = [(item, wrap(draw, item, f_bul, width - 34)) for item in bullets]

    total = 40
    total += len(title_lines) * int(f_title.size * 1.16) + 18
    total += len(sub_lines) * int(f_sub.size * 1.42) + 34
    for _, lines in bullet_lines:
        total += len(lines) * int(f_bul.size * 1.44) + 10

    y = top + max(0, (height - total) // 2)
    draw.rounded_rectangle([x, y + 6, x + 56, y + 12], radius=3, fill=accent)
    y += 40

    for line in title_lines:
        draw.text((x, y), line, font=f_title, fill=INK)
        y += int(f_title.size * 1.16)
    y += 18

    for line in sub_lines:
        draw.text((x, y), line, font=f_sub, fill=DIM)
        y += int(f_sub.size * 1.42)
    y += 34

    for item, lines in bullet_lines:
        for i, line in enumerate(lines):
            if i == 0:
                draw.ellipse([x + 2, y + 12, x + 12, y + 22], fill=accent)
            draw.text((x + 30, y), line, font=f_bul, fill=(214, 222, 228))
            y += int(f_bul.size * 1.44)
        y += 10


SHOTS = [
    dict(
        out="1-hero.png",
        render="en-convert-orbit.png",
        caption=dict(
            title="Drag a file. Pick an action.",
            sub="Drop it on a screen edge and a ring of actions appears under the pointer — "
                "no window to find, no menu to hunt through.",
            bullets=[
                "Images, PDFs, video, audio and Office documents",
                "Results land beside the original, never overwriting it",
                "Everything runs on your PC: no uploads, no accounts",
            ],
        ),
    ),
    dict(
        out="2-actions.png",
        render="en-actions.png",
        caption=dict(
            title="Everyday file work, one gesture",
            sub="Convert, compress, crop, resize, rotate, split, merge and rename — the same drag "
                "for all of them.",
            bullets=[
                "HEIC to JPG, PNG to WebP, DOCX to PDF, MP4 to GIF",
                "PDF pages split, merged and rasterised locally",
                "Safe by default: a source file is never modified",
            ],
        ),
    ),
    dict(
        out="3-look.png",
        render="en-settings.png",
        caption=dict(
            title="Make it look like your app",
            sub="Layout, accent, density, trigger key and language, with a live preview of the real "
                "renderer.",
            bullets=[
                "Six presets, three ring layouts, four preview corners",
                "Follows the Windows display language, or pin English or Arabic",
                "Looks export as a small, readable JSON file",
            ],
        ),
        max_h=980,
    ),
    dict(
        out="4-arabic.png",
        render="ar-convert-donut.png",
        caption=dict(
            title="Arabic, right to left",
            sub="The whole interface is translated and mirrored, while format names stay readable.",
            bullets=[
                "Follows Windows' display language as you change it",
                "RTL layout for every window, not just the ring",
                "Format names stay Latin: HEIC, MP4, PDF",
            ],
        ),
    ),
]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, "packaging", "StoreScreenshots"))
    ap.add_argument("--size", default="1920x1080")
    args = ap.parse_args()
    width, height = (int(v) for v in args.size.lower().split("x"))
    if width < 1366 or height < 768:
        print("the Store requires at least 1366x768 for a desktop screenshot", file=sys.stderr)
        return 2
    os.makedirs(args.out, exist_ok=True)

    written = []
    for shot in SHOTS:
        render = os.path.join(IMAGES, shot["render"])
        if not os.path.exists(render):
            print(f"missing render: {render}", file=sys.stderr)
            return 1
        accent = accent_from(render)
        canvas = backdrop((width, height), accent).convert("RGBA")
        paste_render(canvas, render, (int(width * 0.50), int(height * 0.04),
                                      int(width * 0.45), int(height * 0.88)))
        caption(canvas, int(width * 0.055), int(height * 0.10), int(width * 0.40),
                int(height * 0.72), accent, **shot["caption"])

        mark = font(FONT_SEMI, 24)
        draw = ImageDraw.Draw(canvas)
        label = "DragDone  ·  no network  ·  originals always kept"
        draw.text((int(width * 0.055), height - 74), label, font=mark, fill=(120, 132, 142))

        path = os.path.join(args.out, shot["out"])
        canvas.convert("RGB").save(path, "PNG", optimize=True)
        written.append(path)
        print(f"{os.path.basename(path)}  {width}x{height}  {os.path.getsize(path) // 1024} KB")

    print(f"\n{len(written)} screenshots in {args.out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
