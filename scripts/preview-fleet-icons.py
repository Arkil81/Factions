#!/usr/bin/env python3
"""Renders the marker + echelon composite at true on-screen size, using MapRenderer's own
arithmetic, so the layout constants can be judged before a client rebuild.

This checks the LAYOUT, not SE's rasteriser: whether three bars collide, whether the marks clear
the marker, whether a dot row reads as a count.
"""
import sys
from pathlib import Path
from PIL import Image

ICONS = Path(__file__).parent / "icons" / "out"
OUT = Path(__file__).parent / "composite_preview.png"

ICON_PX = 18.0                    # MapRenderer.IconPixels
MARK_FRACTION = float(sys.argv[1]) if len(sys.argv) > 1 else 0.38
SPACING_DOT = float(sys.argv[2]) if len(sys.argv) > 2 else 0.46
SPACING_BAR = float(sys.argv[3]) if len(sys.argv) > 3 else 0.46
GAP_FRACTION = 0.20

ZOOM = 8                          # nearest-neighbour, so pixels are countable
CELL_W, CELL_H = 46, 40

# convoy size -> (mark file, count), mirroring FleetEchelon.For
def echelon(n):
    if n >= 18: return ("echelon_cross", 1)
    if n >= 12: return ("echelon_bar", 3)
    if n >= 7:  return ("echelon_bar", 2)
    if n >= 4:  return ("echelon_bar", 1)
    if n == 3:  return ("echelon_dot", 3)
    if n == 2:  return ("echelon_dot", 2)
    return (None, 0)

SIZES = [1, 2, 3, 4, 7, 12, 18]
KINDS = ["raid", "guard", "patrol", "trade", "haul", "mine", "generic"]
TINTS = {"neutral": (255, 255, 255)}


def load(name, px, tint):
    """Premultiplied white source -> tinted, resampled to px. Matches the engine: the sprite
    colour multiplies the (already premultiplied) texel."""
    img = Image.open(ICONS / f"{name}.png").convert("RGBA")
    img = img.resize((max(1, round(px)), max(1, round(px))), Image.LANCZOS)
    r, g, b, a = img.split()
    tr, tg, tb = tint
    return Image.merge("RGBA", (
        r.point(lambda v: v * tr // 255),
        g.point(lambda v: v * tg // 255),
        b.point(lambda v: v * tb // 255),
        a))


def cell(kind, size, tint, bg):
    canvas = Image.new("RGBA", (CELL_W, CELL_H), bg)
    cx, cy = CELL_W // 2, CELL_H // 2 + 6

    icon = load(f"fleet_{kind}", ICON_PX, tint)
    canvas.alpha_composite(icon, (cx - icon.width // 2, cy - icon.height // 2))

    mark_name, count = echelon(size)
    if mark_name:
        mark_px = ICON_PX * MARK_FRACTION
        spacing = ICON_PX * (SPACING_DOT if "dot" in mark_name else SPACING_BAR)
        mark = load(mark_name, mark_px, tint)
        first = cx - spacing * (count - 1) / 2.0
        y = cy - ICON_PX * (0.5 + GAP_FRACTION) - mark_px / 2.0
        for i in range(count):
            x = first + spacing * i
            canvas.alpha_composite(mark, (round(x - mark.width / 2), round(y - mark.height / 2)))
    return canvas


def main():
    from PIL import ImageDraw
    rows = len(TINTS) * len(KINDS)
    sheet = Image.new("RGBA", (CELL_W * len(SIZES) * ZOOM + 90, CELL_H * rows * ZOOM + 24),
                      (18, 18, 20, 255))
    draw = ImageDraw.Draw(sheet)
    draw.text((6, 6), f"mark={MARK_FRACTION}  dotSpacing={SPACING_DOT}  barSpacing={SPACING_BAR}",
              fill=(220, 220, 220))

    y = 24
    for tname, tint in TINTS.items():
        bg = (12, 12, 14, 255)
        for kind in KINDS:
            draw.text((6, y + 8), f"{kind[:7]}/{tname[:3]}", fill=(150, 150, 160))
            x = 90
            for size in SIZES:
                c = cell(kind, size, tint, bg).resize(
                    (CELL_W * ZOOM, CELL_H * ZOOM), Image.NEAREST)
                sheet.alpha_composite(c, (x, y))
                x += CELL_W * ZOOM
            y += CELL_H * ZOOM
    sheet.convert("RGB").save(OUT)
    print("wrote", OUT, sheet.size)


if __name__ == "__main__":
    main()
