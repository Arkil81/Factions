#!/usr/bin/env python3
"""
Tactical-map icon generator for the Factions client plugin.

Emits 64x64 fleet-kind glyphs and 32x32 echelon marks as WHITE, PREMULTIPLIED-ALPHA
RGBA PNGs on a fully transparent background.

Contract enforced by verify_premultiplied():
  * every pixel satisfies R == G == B == A   (white, premultiplied)
  * every pixel with A == 0 is exactly (0, 0, 0, 0)

Shapes are rasterised with cairo into an A8 (coverage-only) surface, so the alpha
channel IS the art and the colour channels are synthesised from it.  That makes the
premultiplication invariant true by construction rather than by correction.

Stroke floor: 8 px at 64x64, 5 px at 32x32 (the map draws these at 18 px / ~7 px).
"""

import math
import os
import struct
import zlib

import cairo
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")

FLEET_SIZE = 64
ECHELON_SIZE = 32

# on-screen sizes the map actually draws these at
FLEET_SCREEN = 18
ECHELON_SCREEN = 7


# --------------------------------------------------------------------------
# geometry helpers
# --------------------------------------------------------------------------

def rounded_poly(ctx, pts, r):
    """Close a polygon with circular fillets of radius r at every vertex."""
    n = len(pts)
    if r <= 0:
        ctx.move_to(*pts[0])
        for p in pts[1:]:
            ctx.line_to(*p)
        ctx.close_path()
        return

    def lerp(a, b, t):
        return (a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t)

    first = None
    for i in range(n):
        prev = pts[(i - 1) % n]
        cur = pts[i]
        nxt = pts[(i + 1) % n]

        d_prev = math.hypot(cur[0] - prev[0], cur[1] - prev[1])
        d_next = math.hypot(nxt[0] - cur[0], nxt[1] - cur[1])
        rr = min(r, d_prev / 2.0, d_next / 2.0)

        a = lerp(cur, prev, rr / d_prev) if d_prev else cur
        b = lerp(cur, nxt, rr / d_next) if d_next else cur

        if first is None:
            ctx.move_to(*a)
            first = a
        else:
            ctx.line_to(*a)
        ctx.curve_to(cur[0], cur[1], cur[0], cur[1], b[0], b[1])
    ctx.close_path()


def star(ctx, cx, cy, r_out, r_in, points, phase=0.0):
    n = points * 2
    for i in range(n):
        r = r_out if i % 2 == 0 else r_in
        a = phase + i * math.pi / points
        x = cx + r * math.cos(a)
        y = cy + r * math.sin(a)
        if i == 0:
            ctx.move_to(x, y)
        else:
            ctx.line_to(x, y)
    ctx.close_path()


# --------------------------------------------------------------------------
# fleet glyphs  (64 x 64, ~3-4 px margin)
# --------------------------------------------------------------------------

def draw_raid(ctx):
    """Broad swept delta, sharp nose up, deep V notch bitten out of the tail.

    The only glyph with a CONCAVE trailing edge, and the only sharp apex that
    sits on a wide base.  Mass matters more than sharpness at 18 px, so the
    arms are kept thick; the notch is 16 px (4.5 px on screen).
    'offensive, pointed, moving fast'.
    """
    rounded_poly(ctx, [(32, 2), (57, 58), (32, 41), (7, 58)], 4.0)
    ctx.fill()


def draw_guard(ctx):
    """Wide bulwark: square shoulders, straight taper, flat chin.

    Wide-and-short (52 x 48) and pointing DOWN, against raid's tall apex.
    Flat top edge is the cue that survives: nothing else in the set is widest
    at the very top.  'stationed, protective'.
    """
    rounded_poly(ctx, [(6, 9), (58, 9), (58, 24), (41, 57), (23, 57), (6, 24)], 3.0)
    ctx.fill()


def draw_patrol(ctx):
    """Thick annulus - the only glyph with a hole in it.

    Outer d 58, inner d 36: an 11 px stroke around a 36 px hole, which lands at
    3.1 px of ring around a 10 px void on screen.  Presence/absence of a hole is
    the most robust cue at this scale.  'a circuit being walked / a rally ring'.
    """
    ctx.set_fill_rule(cairo.FILL_RULE_EVEN_ODD)
    ctx.arc(32, 32, 29, 0, 2 * math.pi)
    ctx.new_sub_path()
    ctx.arc(32, 32, 18, 0, 2 * math.pi)
    ctx.fill()
    ctx.set_fill_rule(cairo.FILL_RULE_WINDING)


def draw_trade(ctx):
    """Tapered freighter hull, blunt flat bow, wide flat stern.

    Deliberately NOT pointed - a chamfered box, tall and narrow (32 x 55).
    Convex everywhere, so it cannot be confused with raid's notched delta.
    'a laden merchant under way'.
    """
    rounded_poly(ctx, [(24, 4), (40, 4), (48, 22), (48, 59), (16, 59), (16, 22)], 4.0)
    ctx.fill()


def draw_haul(ctx):
    """Wide low barge slab, flat top and bottom, blunt angled ends.

    The only strongly horizontal glyph (58 x 30, aspect 1.9).  Same polygonal
    'hull' family as trade, rotated onto the other axis and fattened.
    'bulk, heavy, slow'.
    """
    rounded_poly(ctx, [(13, 17), (51, 17), (61, 32), (51, 47), (13, 47), (3, 32)], 3.0)
    ctx.fill()


def draw_mine(ctx):
    """Solid gem / ore crystal - a tall rhombus.

    Four hard corners on the axes: the only glyph that is pointed at BOTH ends,
    so it reads as a mineral rather than a heading.  'extraction, industrial'.
    """
    rounded_poly(ctx, [(32, 3), (56, 32), (32, 61), (8, 32)], 4.0)
    ctx.fill()


def draw_generic(ctx):
    """Plain solid disc - a bare 'contact here' blip.

    No corners, no axis, no meaning beyond presence.  Separated from patrol
    purely by the absence of the hole, and held deliberately smaller (d 36)
    than the mine gem and the trade hull so mass alone separates it from them.
    """
    ctx.arc(32, 32, 18, 0, 2 * math.pi)
    ctx.fill()


# --------------------------------------------------------------------------
# echelon marks  (32 x 32, stroke floor 5 px)
# --------------------------------------------------------------------------

def draw_echelon_dot(ctx):
    """Filled circle, d 22. Drawn 1-3 times for 2-3 hulls."""
    ctx.arc(16, 16, 11, 0, 2 * math.pi)
    ctx.fill()


def draw_echelon_bar(ctx):
    """Upright bar, 12 x 26. Drawn 1-3 times for 4-17 hulls."""
    rounded_poly(ctx, [(10, 3), (22, 3), (22, 29), (10, 29)], 2.0)
    ctx.fill()


def draw_echelon_cross(ctx):
    """Saltire, 10 px stroke. Drawn once, for 18+ hulls."""
    ctx.set_line_cap(cairo.LINE_CAP_ROUND)
    ctx.set_line_width(10.0)
    ctx.move_to(7, 7)
    ctx.line_to(25, 25)
    ctx.stroke()
    ctx.move_to(25, 7)
    ctx.line_to(7, 25)
    ctx.stroke()


def draw_radio_ring(ctx):
    """The ATC speaker mark: a dashed ring drawn around a station's HUD icon while its controller
    speaks (docs/atc-voice.md). Twelve dashes, half the circumference inked."""
    c, r, dashes = 32.0, 27.0, 12
    ctx.set_line_width(5.0)
    ctx.set_line_cap(cairo.LINE_CAP_ROUND)
    step = 2 * math.pi / dashes
    for i in range(dashes):
        a0 = i * step + step * 0.22
        ctx.new_sub_path()
        ctx.arc(c, c, r, a0, a0 + step * 0.56)
        ctx.stroke()


RING = [
    ("radio_ring", draw_radio_ring),
]


FLEET = [
    ("fleet_raid", draw_raid),
    ("fleet_guard", draw_guard),
    ("fleet_patrol", draw_patrol),
    ("fleet_trade", draw_trade),
    ("fleet_haul", draw_haul),
    ("fleet_mine", draw_mine),
    ("fleet_generic", draw_generic),
]

ECHELON = [
    ("echelon_dot", draw_echelon_dot),
    ("echelon_bar", draw_echelon_bar),
    ("echelon_cross", draw_echelon_cross),
]


# --------------------------------------------------------------------------
# rasterise -> premultiplied white RGBA
# --------------------------------------------------------------------------

def render_mask(draw_fn, size):
    """Rasterise into an A8 coverage surface and return `size*size` bytes."""
    surf = cairo.ImageSurface(cairo.FORMAT_A8, size, size)
    ctx = cairo.Context(surf)
    ctx.set_antialias(cairo.ANTIALIAS_BEST)
    ctx.set_source_rgba(0, 0, 0, 1)   # A8 keeps alpha only
    draw_fn(ctx)
    surf.flush()

    stride = surf.get_stride()
    data = bytes(surf.get_data())
    rows = [data[y * stride:y * stride + size] for y in range(size)]
    return b"".join(rows)


def mask_to_image(mask, size):
    """White premultiplied RGBA: R = G = B = A = coverage."""
    a = Image.frombytes("L", (size, size), mask)
    return Image.merge("RGBA", (a, a, a, a))


def write_png(img, path):
    img.save(path, "PNG", optimize=True)


# --------------------------------------------------------------------------
# verification
# --------------------------------------------------------------------------

def verify_premultiplied(path):
    """Re-read the written file and check the two invariants."""
    im = Image.open(path)
    if im.mode != "RGBA":
        return False, "mode is %s, not RGBA" % im.mode
    px = im.load()
    w, h = im.size
    bad_rgba = 0
    bad_zero = 0
    first_bad = None
    opaque = 0
    for y in range(h):
        for x in range(w):
            r, g, b, a = px[x, y]
            if not (r == g == b == a):
                bad_rgba += 1
                if first_bad is None:
                    first_bad = (x, y, r, g, b, a)
            if a == 0 and (r or g or b):
                bad_zero += 1
                if first_bad is None:
                    first_bad = (x, y, r, g, b, a)
            if a > 0:
                opaque += 1
    ok = bad_rgba == 0 and bad_zero == 0
    detail = "%dx%d  covered=%d/%d (%.0f%%)" % (w, h, opaque, w * h, 100.0 * opaque / (w * h))
    if not ok:
        detail += "  R!=G!=B!=A:%d  A0-with-colour:%d  first=%s" % (bad_rgba, bad_zero, first_bad)
    return ok, detail


# --------------------------------------------------------------------------
# contact sheet
# --------------------------------------------------------------------------

def composite(img, bg):
    """Composite a PREMULTIPLIED RGBA over an opaque bg colour."""
    w, h = img.size
    out = Image.new("RGB", (w, h), bg)
    src = img.load()
    dst = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, a = src[x, y]
            inv = (255 - a) / 255.0
            dst[x, y] = (
                min(255, int(bg[0] * inv + r)),
                min(255, int(bg[1] * inv + g)),
                min(255, int(bg[2] * inv + b)),
            )
    return out


def downscale(img, target):
    """Premultiplied alpha resamples linearly, so a plain LANCZOS is correct."""
    return img.resize((target, target), Image.LANCZOS)


def get_font(sz):
    for p in ("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
              "/usr/share/fonts/TTF/DejaVuSans.ttf"):
        if os.path.exists(p):
            try:
                return ImageFont.truetype(p, sz)
            except Exception:
                pass
    return ImageFont.load_default()


def build_contact_sheet(entries, path):
    """entries: list of (name, rgba_image, source_size, screen_size)."""
    COL = 104
    PADX = 14
    TOP = 34
    ROW_LABEL = 18

    grey = (128, 128, 128)
    black = (12, 12, 14)

    # per-panel rows: big / true-size / true-size x4
    panel_rows = [("source", None), ("true size", None), ("true size x4", None)]
    ROW_H = [76, 30, 84]
    PANEL_H = ROW_LABEL + sum(r + 8 for r in ROW_H) + 14

    W = PADX * 2 + COL * len(entries)
    H = TOP + PANEL_H * 2 + 20

    sheet = Image.new("RGB", (W, H), (32, 32, 36))
    d = ImageDraw.Draw(sheet)
    f_title = get_font(15)
    f_name = get_font(10)
    f_row = get_font(11)

    d.text((PADX, 8), "Factions tactical-map icons - white premultiplied RGBA, "
                      "shown on mid-grey and near-black", font=f_title, fill=(235, 235, 235))

    for pi, (bg, bgname) in enumerate(((grey, "mid-grey #808080"), (black, "near-black #0c0c0e"))):
        py = TOP + pi * PANEL_H
        d.rectangle([PADX - 6, py, W - PADX + 6, py + PANEL_H - 8], fill=bg)
        d.text((PADX - 2, py + 3), bgname, font=f_name,
               fill=(20, 20, 20) if pi == 0 else (170, 170, 170))
        ink = (20, 20, 20) if pi == 0 else (150, 150, 150)

        for ri, (rowname, _) in enumerate(panel_rows):
            ry = py + ROW_LABEL + sum(r + 8 for r in ROW_H[:ri])
            d.text((PADX - 2, ry + ROW_H[ri] - 12), rowname, font=f_row, fill=ink)

            for ci, (name, img, src, scr) in enumerate(entries):
                cx = PADX + ci * COL + COL // 2
                if ri == 0:
                    tile = composite(img, bg)
                elif ri == 1:
                    tile = composite(downscale(img, scr), bg)
                else:
                    small = downscale(img, scr)
                    tile = composite(small, bg).resize((scr * 4, scr * 4), Image.NEAREST)
                sheet.paste(tile, (cx - tile.size[0] // 2, ry + (ROW_H[ri] - tile.size[1]) // 2))

        # names under the top row of each panel
        for ci, (name, img, src, scr) in enumerate(entries):
            cx = PADX + ci * COL + COL // 2
            tw = d.textlength(name, font=f_name)
            d.text((cx - tw / 2, py + ROW_LABEL + ROW_H[0] - 2), name, font=f_name, fill=ink)

    sheet.save(path, "PNG")
    return path


# --------------------------------------------------------------------------

def main():
    if not os.path.isdir(OUT):
        os.makedirs(OUT)

    entries = []
    results = []

    for name, fn in FLEET:
        mask = render_mask(fn, FLEET_SIZE)
        img = mask_to_image(mask, FLEET_SIZE)
        p = os.path.join(OUT, name + ".png")
        write_png(img, p)
        ok, detail = verify_premultiplied(p)
        results.append((name + ".png", ok, detail))
        entries.append((name.replace("fleet_", ""), img, FLEET_SIZE, FLEET_SCREEN))

    for name, fn in ECHELON:
        mask = render_mask(fn, ECHELON_SIZE)
        img = mask_to_image(mask, ECHELON_SIZE)
        p = os.path.join(OUT, name + ".png")
        write_png(img, p)
        ok, detail = verify_premultiplied(p)
        results.append((name + ".png", ok, detail))
        entries.append((name.replace("echelon_", "ech "), img, ECHELON_SIZE, ECHELON_SCREEN))

    for name, fn in RING:
        mask = render_mask(fn, FLEET_SIZE)
        img = mask_to_image(mask, FLEET_SIZE)
        p = os.path.join(OUT, name + ".png")
        write_png(img, p)
        ok, detail = verify_premultiplied(p)
        results.append((name + ".png", ok, detail))
        entries.append((name, img, FLEET_SIZE, FLEET_SCREEN))

    print("premultiplied-alpha verification (R==G==B==A, and A==0 => 0,0,0,0)")
    print("-" * 78)
    allok = True
    for fname, ok, detail in results:
        allok = allok and ok
        print("  %-22s %s   %s" % (fname, "PASS" if ok else "FAIL", detail))
    print("-" * 78)
    print("  %d files, %s" % (len(results), "ALL PASS" if allok else "FAILURES PRESENT"))

    sheet = build_contact_sheet(entries, os.path.join(HERE, "CONTACT_SHEET.png"))
    print("  contact sheet -> %s" % sheet)
    return 0 if allok else 1


if __name__ == "__main__":
    raise SystemExit(main())
