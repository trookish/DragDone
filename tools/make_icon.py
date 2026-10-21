"""Generate Assets/DragDone.ico with the standard library only.

Design: dark navy rounded tile, a teal three-quarter ring split into sectors
(the radial menu), and a white hub dot (the dropped file).
Run: python tools/make_icon.py
"""
import math
import struct
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parents[1] / "src" / "DragDone" / "Assets" / "DragDone.ico"
SIZES = [16, 24, 32, 48, 64, 128, 256]
SS = 4  # supersampling factor

BG_TOP = (21, 44, 82)
BG_BOTTOM = (9, 20, 40)
ACCENT = (46, 196, 166)
ACCENT_DIM = (34, 120, 128)
WHITE = (245, 248, 252)


def rounded_rect_alpha(x, y, s, r):
    # signed distance to a rounded square of size s with radius r
    cx = min(max(x, r), s - r)
    cy = min(max(y, r), s - r)
    d = math.hypot(x - cx, y - cy)
    return 1.0 if d <= r else 0.0


def sample(px, py, s):
    """Colour (r,g,b,a) of one supersampled point in a size-s icon."""
    radius = s * 0.22
    if rounded_rect_alpha(px, py, s, radius) == 0.0:
        return (0, 0, 0, 0)
    t = py / s
    col = tuple(BG_TOP[i] * (1 - t) + BG_BOTTOM[i] * t for i in range(3))
    cx = cy = s / 2
    dx, dy = px - cx, py - cy
    dist = math.hypot(dx, dy)
    outer, inner = s * 0.36, s * 0.22
    if inner <= dist <= outer:
        ang = (math.degrees(math.atan2(dy, dx)) + 90) % 360  # 0 at top, clockwise
        gap = 9 if s >= 32 else 14
        sector = int(ang // 60)
        local = ang - sector * 60
        if gap / 2 <= local <= 60 - gap / 2:
            col = ACCENT if sector in (0, 1) else ACCENT_DIM
    if dist <= s * 0.11:
        col = WHITE
    return (*col, 255)


def render(s):
    big = s * SS
    rows = []
    for y in range(s):
        row = bytearray()
        for x in range(s):
            acc = [0.0, 0.0, 0.0, 0.0]
            for sy in range(SS):
                for sx in range(SS):
                    r, g, b, a = sample((x * SS + sx + 0.5) / SS, (y * SS + sy + 0.5) / SS, s)
                    acc[0] += r * a
                    acc[1] += g * a
                    acc[2] += b * a
                    acc[3] += a
            n = SS * SS
            a = acc[3] / n
            if a > 0:
                row += bytes((round(acc[0] / acc[3]), round(acc[1] / acc[3]), round(acc[2] / acc[3]), round(a)))
            else:
                row += b"\0\0\0\0"
        rows.append(bytes(row))
    _ = big
    return rows


def png_bytes(rows, s):
    raw = b"".join(b"\0" + r for r in rows)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", s, s, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main():
    images = [(s, png_bytes(render(s), s)) for s in SIZES]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b""
    data = b""
    for s, png in images:
        dim = 0 if s >= 256 else s
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_bytes(header + entries + data)
    (OUT.parent / "DragDone-256.png").write_bytes(images[-1][1])
    print(f"wrote {OUT} ({len(header + entries + data)} bytes)")


if __name__ == "__main__":
    main()
