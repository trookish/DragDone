"""Generate the Microsoft Store logo art from the app icon's own drawing code.

Partner Center wants art that is NOT in the package: 1:1 box art (1080x1080 or larger), optional
9:16 poster art (1440x2160), and the optional 300/150/71 square display icons. Everything here is
derived from tools/make_icon.py, so the mark on the Store page is the same mark on the taskbar.

Run: python packaging/make_store_logos.py
"""
from __future__ import annotations

import importlib.util
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "packaging", "StoreLogos")

BG_TOP = (21, 44, 82)
BG_BOTTOM = (9, 20, 40)
ACCENT = (46, 196, 166)
WHITE = (245, 248, 252)
DIM = (150, 172, 196)

FONT_SEMI = r"C:\Windows\Fonts\seguisb.ttf"
FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_TEXT = r"C:\Windows\Fonts\segoeui.ttf"


def icon_module():
    spec = importlib.util.spec_from_file_location("make_icon", os.path.join(ROOT, "tools", "make_icon.py"))
    if spec is None or spec.loader is None:
        raise SystemExit("tools/make_icon.py not found")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def tile(icon, size: int) -> Image.Image:
    """The rounded app tile at any size, rendered by the icon's own sampler."""
    icon.SS = 3
    rows = icon.render(size)
    return Image.frombytes("RGBA", (size, size), b"".join(rows))


def gradient(size: tuple[int, int], top, bottom) -> Image.Image:
    w, h = size
    im = Image.new("RGB", size)
    draw = ImageDraw.Draw(im)
    for y in range(h):
        t = y / max(h - 1, 1)
        draw.line([(0, y), (w, y)], fill=tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)))
    return im


def glow(size: tuple[int, int], centre: tuple[int, int], radius: int, colour) -> Image.Image:
    halo = Image.new("RGB", size, (0, 0, 0))
    draw = ImageDraw.Draw(halo)
    for step in range(radius, 0, -6):
        t = 1 - step / radius
        draw.ellipse([centre[0] - step, centre[1] - step, centre[0] + step, centre[1] + step],
                     fill=tuple(int(colour[i] * 0.09 * t) for i in range(3)))
    from PIL import ImageChops

    return ImageChops.add(halo.filter(ImageFilter.GaussianBlur(radius // 4)), Image.new("RGB", size))


def box_art(icon, size: int) -> Image.Image:
    """1:1 box art: the tile itself, full bleed, on the Store's own background."""
    return tile(icon, size)


def poster(icon, width: int = 1440, height: int = 2160) -> Image.Image:
    """9:16 poster art: the mark over a deeper gradient so the tile reads as a tile.

    The tile's own top colour is (21, 44, 82); a background that darkens past it keeps the rounded
    silhouette visible instead of dissolving it into the canvas.
    """
    dark_top, dark_bottom = (13, 27, 50), (4, 9, 18)
    base = gradient((width, height), dark_top, dark_bottom).convert("RGB")
    mark_size = 780
    mark = tile(icon, mark_size)
    mark = mark.resize((mark_size, mark_size), Image.LANCZOS)

    centre = (width // 2, int(height * 0.36))
    halo = glow((width, height), centre, int(width * 0.55), ACCENT)
    from PIL import ImageChops

    base = ImageChops.add(base, halo)
    base = base.convert("RGBA")

    x = (width - mark_size) // 2
    y = centre[1] - mark_size // 2
    shade = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    ImageDraw.Draw(shade).ellipse(
        [x + 30, y + mark_size * 0.82, x + mark_size - 30, y + mark_size * 1.02],
        fill=(0, 0, 0, 120))
    base = Image.alpha_composite(base, shade.filter(ImageFilter.GaussianBlur(48)))

    # A hairline rim keeps the tile's silhouette from dissolving into a background of the same family.
    rim = Image.new("RGBA", mark.size, (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle(
        [0, 0, mark_size - 1, mark_size - 1], radius=int(mark_size * 0.22),
        outline=(255, 255, 255, 34), width=3)
    mark = Image.alpha_composite(mark, rim.filter(ImageFilter.GaussianBlur(1)))
    base.paste(mark, (x, y), mark)

    draw = ImageDraw.Draw(base)
    word = ImageFont.truetype(FONT_BOLD, 128)
    tagline = ImageFont.truetype(FONT_SEMI, 52)
    fine = ImageFont.truetype(FONT_TEXT, 44)

    def centred(text: str, font: ImageFont.FreeTypeFont, top: int, fill):
        w = draw.textlength(text, font=font)
        draw.text(((width - w) / 2, top), text, font=font, fill=fill)

    centred("DragDone", word, int(height * 0.60), WHITE)

    rule_y = int(height * 0.685)
    draw.rounded_rectangle([width / 2 - 70, rule_y, width / 2 + 70, rule_y + 7], radius=3, fill=ACCENT)
    centred("Drag. Press. Done.", tagline, int(height * 0.715), ACCENT)
    centred("Nothing uploaded. Originals never overwritten.", fine, int(height * 0.795), (176, 196, 214))
    return base.convert("RGB")


def main() -> int:
    icon = icon_module()
    os.makedirs(OUT, exist_ok=True)
    written = []

    for size, name in ((1080, "BoxArt-1080x1080.png"), (2160, "BoxArt-2160x2160.png")):
        art = box_art(icon, 1080)
        if size != 1080:
            art = art.resize((size, size), Image.LANCZOS)
        path = os.path.join(OUT, name)
        art.save(path, "PNG", optimize=True)
        written.append((name, size, size, path))

    art = poster(icon, 1440, 2160)
    path = os.path.join(OUT, "PosterArt-1440x2160.png")
    art.save(path, "PNG", optimize=True)
    written.append(("PosterArt-1440x2160.png", 1440, 2160, path))

    for size in (300, 150, 71):
        art = tile(icon, size)
        name = f"StoreTile-{size}x{size}.png"
        path = os.path.join(OUT, name)
        art.save(path, "PNG", optimize=True)
        written.append((name, size, size, path))

    for name, w, h, path in written:
        print(f"  {name:32} {w}x{h}  {os.path.getsize(path) // 1024} KB")
    print(f"\nwrote {len(written)} logo images to {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
