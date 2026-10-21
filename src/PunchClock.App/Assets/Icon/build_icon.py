"""Rebuild ../PunchClock.ico from the SVGs in this folder.

    python3 -m pip install cairosvg pillow
    python3 src/PunchClock.App/Assets/Icon/build_icon.py

Small sizes come from their own pixel-aligned drawings; scaling the full
drawing down to 16px turns the clock hands into a grey smudge. Sizes below
256 are stored as 32-bit BMP images and 256 as PNG, the layout Windows
Explorer, the WPF title bar and Inno Setup all read.
"""
import io
import pathlib
import struct

import cairosvg
from PIL import Image

HERE = pathlib.Path(__file__).resolve().parent
OUTPUT = HERE.parent / "PunchClock.ico"
SOURCES = {
    16: "punchclock-16.svg",
    20: "punchclock-24.svg",
    24: "punchclock-24.svg",
    32: "punchclock-32.svg",
    40: "punchclock.svg",
    48: "punchclock.svg",
    64: "punchclock.svg",
    128: "punchclock.svg",
    256: "punchclock.svg",
}


def render(size: int) -> Image.Image:
    png = cairosvg.svg2png(url=str(HERE / SOURCES[size]), output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def bmp_entry(im: Image.Image) -> bytes:
    """BITMAPINFOHEADER + bottom-up BGRA pixels + 1bpp AND mask, as ICO expects."""
    w, h = im.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = im.getpixel((x, y))
            pixels += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if im.getpixel((x, y))[3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    return header + bytes(pixels) + bytes(mask)


def png_entry(im: Image.Image) -> bytes:
    buf = io.BytesIO()
    im.save(buf, format="PNG", optimize=True)
    return buf.getvalue()


def main() -> None:
    sizes = sorted(SOURCES)
    blobs = [png_entry(render(s)) if s >= 256 else bmp_entry(render(s)) for s in sizes]
    offset = 6 + 16 * len(sizes)
    out = bytearray(struct.pack("<HHH", 0, 1, len(sizes)))
    for size, blob in zip(sizes, blobs):
        dim = 0 if size >= 256 else size  # 0 means 256 in an ICO directory
        out += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    for blob in blobs:
        out += blob
    OUTPUT.write_bytes(out)
    print(f"wrote {OUTPUT} ({len(out)} bytes, sizes {sizes})")


if __name__ == "__main__":
    main()
