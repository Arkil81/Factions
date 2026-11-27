#!/usr/bin/env python3
"""Renders the fleet-icon figures for the players' guide from the shipped icon art.

Usage:
    python3 scripts/fleet-icons/manual-figures.py

Reads   scripts/fleet-icons/*.png            (the same files gen-fleet-icons.py ships)
Writes  scripts/public-export/overlay/docs/images/icons/*.png

The art is white, premultiplied alpha, so its alpha channel is the coverage. Each figure tints it
the way the client does (src/FactionsUI/Palette.cs) and lays echelon marks out with the fractions
in src/FactionsUI/FleetIcons.cs, on a dark tile standing in for space. Re-run after changing the
art, the palette or those fractions.
"""

from pathlib import Path

from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent.parent
ART = REPO / "scripts" / "fleet-icons"
OUT = REPO / "scripts" / "public-export" / "overlay" / "docs" / "images" / "icons"

BACKGROUND = (14, 17, 24, 255)
TILE = 72          # figure height in pixels
MARKER = 36        # marker size within a tile: twice the map's 18 px, so it reads in a browser
SUPERSAMPLE = 4    # for the rings, which are drawn here rather than read from art

# src/FactionsUI/Palette.cs
OWN = (90, 160, 255)
NEUTRAL = (255, 255, 255)
ENEMY = (230, 60, 60)
FRIENDLY = (80, 210, 100)
UNKNOWN = (128, 128, 128)

# src/FactionsUI/Map/MapRenderer.cs
ATTACK_RING = (255, 107, 74)
DEFENCE_RING = (93, 173, 226)

# src/FactionsUI/FleetIcons.cs
MARK_FRACTION = 0.38
SPACING_FRACTION = 0.46
GAP_FRACTION = 0.20


def tile(width=TILE, height=TILE):
    return Image.new("RGBA", (width, height), BACKGROUND)


def stamp(canvas, name, centre, size, colour, alpha=1.0):
    """Draws one art file, tinted, centred at centre."""
    art = Image.open(ART / (name + ".png")).convert("RGBA").resize((size, size), Image.LANCZOS)
    coverage = art.getchannel("A").point(lambda a: int(a * alpha))
    ink = Image.new("RGBA", art.size, colour + (255,))
    canvas.paste(ink, (int(round(centre[0] - size / 2)), int(round(centre[1] - size / 2))), coverage)


def marks(canvas, glyph, count, centre, colour):
    """FleetIcons echelon layout: a row of marks above the marker, in the marker's colour."""
    size = int(round(MARKER * MARK_FRACTION))
    spacing = MARKER * SPACING_FRACTION
    first = centre[0] - spacing * (count - 1) * 0.5
    y = centre[1] - MARKER * (0.5 + GAP_FRACTION) - size * 0.5
    for i in range(count):
        stamp(canvas, glyph, (first + spacing * i, y), size, colour)


def ring(canvas, centre, radius, colour, width, alpha):
    big = Image.new("L", (canvas.width * SUPERSAMPLE, canvas.height * SUPERSAMPLE), 0)
    d = ImageDraw.Draw(big)
    cx, cy, r = centre[0] * SUPERSAMPLE, centre[1] * SUPERSAMPLE, radius * SUPERSAMPLE
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=int(255 * alpha), width=width * SUPERSAMPLE)
    coverage = big.resize(canvas.size, Image.LANCZOS)
    canvas.paste(Image.new("RGBA", canvas.size, colour + (255,)), (0, 0), coverage)


def save(image, name):
    OUT.mkdir(parents=True, exist_ok=True)
    image.save(OUT / (name + ".png"), optimize=True)


def main():
    centre = (TILE / 2, TILE / 2)
    low = (TILE / 2, TILE * 0.60)   # room above for echelon marks

    for glyph in ("raid", "guard", "patrol", "trade", "haul", "mine", "generic"):
        image = tile()
        stamp(image, "fleet_" + glyph, centre, MARKER, NEUTRAL)
        save(image, "fleet-" + glyph)

    for name, colour in (("own", OWN), ("friendly", FRIENDLY), ("neutral", NEUTRAL),
                         ("enemy", ENEMY), ("unknown", UNKNOWN)):
        image = tile()
        stamp(image, "fleet_trade" if name != "unknown" else "fleet_generic", centre, MARKER, colour)
        save(image, "colour-" + name)

    for name, glyph, count in (("1", None, 0), ("2", "echelon_dot", 2), ("3", "echelon_dot", 3),
                               ("4-6", "echelon_bar", 1), ("7-11", "echelon_bar", 2),
                               ("12-17", "echelon_bar", 3), ("18", "echelon_cross", 1)):
        image = tile()
        stamp(image, "fleet_patrol", low, MARKER, NEUTRAL)
        if glyph:
            marks(image, glyph, count, low, NEUTRAL)
        save(image, "echelon-" + name)

    image = tile()
    stamp(image, "fleet_raid", centre, MARKER, ENEMY)
    ring(image, centre, MARKER * 0.85, ATTACK_RING, 2, 0.95)
    save(image, "ring-engaging")

    image = tile()
    stamp(image, "fleet_guard", centre, MARKER, OWN)
    ring(image, centre, MARKER * 0.85, DEFENCE_RING, 2, 0.75)
    save(image, "ring-holding")

    image = tile()
    stamp(image, "fleet_raid", low, MARKER, ENEMY)
    marks(image, "echelon_bar", 2, low, ENEMY)
    save(image, "example-enemy-raid")


if __name__ == "__main__":
    main()
