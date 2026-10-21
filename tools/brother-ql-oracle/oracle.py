"""Writes 1-bit test bitmaps and the QL-810W jobs brother_ql 0.9.4 makes from them.

Usage: python oracle.py OUT_DIR

Each fixture is <input>.<variant>[.<roll>].bin, made from <input>.png ("red" fixtures also take the red
plane from <input>.red.png). The roll is omitted for 62 mm continuous tape.
"""
import hashlib
import json
import os
import sys
from importlib.metadata import version

from PIL import Image, ImageDraw, ImageOps
from brother_ql.conversion import convert
from brother_ql.raster import BrotherQLRaster

MODEL = "QL-810W"


def blank(w, h):
    return Image.new("1", (w, h), 1)


def border(w, h):
    im = blank(w, h)
    ImageDraw.Draw(im).rectangle([0, 0, w - 1, h - 1], outline=0)
    return im


def checker(w, h, cell=4):
    im = blank(w, h)
    for y in range(h):
        for x in range(w):
            if (x // cell + y // cell) % 2 == 0:
                im.putpixel((x, y), 0)
    return im


def edge_columns(w, h):
    im = blank(w, h)
    for y in range(h):
        im.putpixel((0, y), 0)
        im.putpixel((w - 1, y), 0)
    return im


def lines(w, h, every):
    im = blank(w, h)
    for y in range(0, h, every):
        ImageDraw.Draw(im).line([0, y, w - 1, y], fill=0)
    return im


def single_row():
    im = blank(696, 1)
    im.putpixel((7, 0), 0)
    im.putpixel((8, 0), 0)
    return im


def solid(w, h):
    return Image.new("1", (w, h), 0)


# (label, roll suffix in the file name, printable width)
CONTINUOUS_62 = ("62", None, 696)
OTHER_ROLLS = [("29", "29mm", 306), ("12", "12mm", 106)]
DIE_CUT = ("62x29", "62mmx29mm", 696, 271)

VARIANTS_62 = {
    "default": ("62", {}),
    "nocut": ("62", {"cut": False}),
    "compress": ("62", {"compress": True}),
    "blackred": ("62red", {"red": True}),
}


def main(out):
    os.makedirs(out, exist_ok=True)
    index = []

    def emit(name, im, variant, label, kw, roll=None, source=None):
        data = convert(BrotherQLRaster(MODEL), [source or im], label, dither=False, **kw)
        fn = f"{name}.{variant}.{roll}.bin" if roll else f"{name}.{variant}.bin"
        open(os.path.join(out, fn), "wb").write(data)
        index.append({"input": f"{name}.png", "options": {"label": label, "model": MODEL, "dither": False, **kw},
                      "output": fn, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)})

    # 62 mm continuous, every variant.
    inputs = {
        "blank-10": blank(696, 10),
        "solid-10": solid(696, 10),
        "border-40": border(696, 40),
        "edge-columns-8": edge_columns(696, 8),
        "single-row-1": single_row(),
        "checker-64": checker(696, 64),
        "tall-3000": lines(696, 3000, 50),
    }
    for name, im in inputs.items():
        im.save(os.path.join(out, f"{name}.png"))
        for variant, (label, kw) in VARIANTS_62.items():
            emit(name, im, variant, label, kw)

    # Other continuous rolls: the margin and the media width change.
    for label, roll, width in OTHER_ROLLS:
        for name, im in {f"border-{roll}": border(width, 40), f"checker-{roll}": checker(width, 24), f"edge-columns-{roll}": edge_columns(width, 8)}.items():
            im.save(os.path.join(out, f"{name}.png"))
            emit(name, im, "default", label, {}, roll)
            emit(name, im, "compress", label, {"compress": True}, roll)

    # A die-cut label: fixed length, media type 0x0B, no margin.
    label, roll, width, height = DIE_CUT
    for name, im in {"border-die-cut": border(width, height), "checker-die-cut": checker(width, height, 8)}.items():
        im.save(os.path.join(out, f"{name}.png"))
        emit(name, im, "default", label, {}, roll)

    # Two colors on DK-22251: pure black and pure red pixels, never overlapping.
    black = blank(696, 60)
    ImageDraw.Draw(black).rectangle([20, 5, 300, 25], fill=0)
    black.putpixel((0, 59), 0)
    red = blank(696, 60)
    ImageDraw.Draw(red).rectangle([350, 30, 690, 55], fill=0)
    red.putpixel((695, 0), 0)
    color = Image.new("RGB", (696, 60), (255, 255, 255))
    color.paste((0, 0, 0), mask=ImageOps.invert(black.convert("L")))
    color.paste((255, 0, 0), mask=ImageOps.invert(red.convert("L")))
    black.save(os.path.join(out, "black-and-red.png"))
    red.save(os.path.join(out, "black-and-red.red.png"))
    emit("black-and-red", black, "red", "62red", {"red": True}, source=color)

    versions = {"python": sys.version.split()[0], "Pillow": version("Pillow"), "brother_ql": version("brother_ql"), "packbits": version("packbits")}
    json.dump({"versions": versions, "fixtures": index}, open(os.path.join(out, "manifest.json"), "w"), indent=2)
    print(f"{len(index)} fixtures written to {out}")


if __name__ == "__main__":
    main(sys.argv[1])
