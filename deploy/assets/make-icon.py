#!/usr/bin/env python3
"""Baut die Launcher-Icons aus der kanonischen Stonetavern-Marke.

Quelle ist bewusst das Marken-Asset und keine eigene Zeichnung: das Icon soll
dieselbe Laterne sein, die auf der Website und in Discord steht.

    python3 deploy/assets/make-icon.py

Schreibt stonetavern-launcher{,-512,-128}.png neben dieses Skript.
"""
from pathlib import Path

from PIL import Image, ImageDraw

BRAND = Path("(internal design notes, not published)"
             "stonetavern-lantern-1024.png")
OUT = Path(__file__).resolve().parent

# Anteil der Kachelhoehe, den die Laterne einnehmen soll. Das Social-Asset ist
# fuer Avatare gebaut und laesst rundherum viel Luft; auf einer App-Kachel liest
# sich diese Luft als Fehler ("das Logo ist zu klein geraten").
FILL = 0.72
CORNER = 0.07   # Radius als Anteil der Kantenlaenge — App-Kachel, keine Pille.
SIZES = ((512, "stonetavern-launcher-512.png"),
         (256, "stonetavern-launcher.png"),
         (128, "stonetavern-launcher-128.png"))


def lantern_bbox(im: Image.Image) -> tuple[int, int, int, int]:
    """Bounding-Box der hellen Laterne. Pergament (236,227,210) liegt weit ueber
    der Schwelle, der dunkle Grund und die Ember-Glut darunter."""
    px = im.load()
    w, h = im.size
    minx, miny, maxx, maxy = w, h, 0, 0
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if r + g + b > 330:
                minx, maxx = min(minx, x), max(maxx, x)
                miny, maxy = min(miny, y), max(maxy, y)
    return minx, miny, maxx, maxy


def main() -> int:
    im = Image.open(BRAND).convert("RGB")
    x0, y0, x1, y1 = lantern_bbox(im)
    side = round((y1 - y0) / FILL)
    cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
    left, top = cx - side // 2, cy - side // 2
    crop = im.crop((left, top, left + side, top + side))

    for size, name in SIZES:
        tile = crop.resize((size, size), Image.LANCZOS).convert("RGBA")
        mask = Image.new("L", (size, size), 0)
        ImageDraw.Draw(mask).rounded_rectangle(
            [0, 0, size - 1, size - 1], radius=round(size * CORNER), fill=255)
        tile.putalpha(mask)
        tile.save(OUT / name)
        print(f"{name}: {size}x{size}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
