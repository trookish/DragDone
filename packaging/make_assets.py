"""Generate the MSIX tile assets from the same drawing code as the app icon.

The glyph is defined once in tools/make_icon.py, so the Store tiles cannot drift away from the
.ico the app and the installer use. Output goes to packaging/Assets/ and is committed: the package
build must not depend on running Python.

Run: python packaging/make_assets.py
"""
from __future__ import annotations

import importlib.util
import struct
import sys
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = Path(__file__).resolve().parent / "Assets"


def load_icon_module():
    spec = importlib.util.spec_from_file_location("make_icon", ROOT / "tools" / "make_icon.py")
    if spec is None or spec.loader is None:
        raise SystemExit("tools/make_icon.py not found")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def encode(width: int, height: int, rows: list[bytes]) -> bytes:
    raw = b"".join(b"\0" + r for r in rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def square(icon, size: int) -> tuple[int, int, list[bytes]]:
    icon.SS = 4 if size <= 96 else 3
    return size, size, icon.render(size)


def wide(icon, glyph: int, width: int, height: int) -> tuple[int, int, list[bytes]]:
    """The square glyph centred on a transparent banner, rather than a stretched one."""
    _, _, source = square(icon, glyph)
    left = (width - glyph) // 2
    top = (height - glyph) // 2
    blank = b"\0" * (width * 4)
    rows: list[bytes] = []
    for y in range(height):
        if top <= y < top + glyph:
            row = bytearray(blank)
            row[left * 4:(left + glyph) * 4] = source[y - top]
            rows.append(bytes(row))
        else:
            rows.append(blank)
    return width, height, rows


def main() -> None:
    icon = load_icon_module()
    OUT.mkdir(parents=True, exist_ok=True)
    written = 0

    squares = {
        "Square44x44Logo.png": 44,
        "Square71x71Logo.png": 71,
        "Square150x150Logo.png": 150,
        "Square310x310Logo.png": 310,
        "StoreLogo.png": 50,
    }
    for name, size in squares.items():
        w, h, rows = square(icon, size)
        (OUT / name).write_bytes(encode(w, h, rows))
        written += 1
        print(f"  {name:44} {size}x{size}")

    # Unplated target sizes: Windows picks one instead of scaling the 44px tile, which is what
    # makes the taskbar and Start-list entry crisp at 125% and 150% display scaling.
    for size in (16, 20, 24, 32, 40, 48, 64):
        w, h, rows = square(icon, size)
        name = f"Square44x44Logo.targetsize-{size}_altform-unplated.png"
        (OUT / name).write_bytes(encode(w, h, rows))
        written += 1
        print(f"  {name:44} {size}x{size}")

    w, h, rows = wide(icon, 150, 310, 150)
    (OUT / "Wide310x150Logo.png").write_bytes(encode(w, h, rows))
    written += 1
    print(f"  {'Wide310x150Logo.png':44} 310x150")

    print(f"wrote {written} assets to {OUT}")


if __name__ == "__main__":
    sys.exit(main())
