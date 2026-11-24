"""Draws the Autodesk Marketplace icon for AB SwitchBack: assets/store_icon_<size>.png.

The store's review refuses text in an app icon (AB MEP Supports, 2026-11), so this one has none:
the AB Adv Tools tile - a blue frame round a dark tile that darkens downwards, as in abadv_256.png
and AB MEP Supports' icon - carrying an element (an isometric cube) between two arrows that chase
each other: picked in one application, found in the other.

    python3 tools/make-store-icon.py          (needs Pillow: pip install pillow)
"""
import math
import os
from PIL import Image, ImageDraw

ACCENT = (31, 111, 235)
TILE_TOP = (19, 32, 50)
TILE_BOTTOM = (8, 15, 26)
STEEL = (150, 175, 205)
WHITE = (255, 255, 255)

S = 1024  # drawn large, then scaled down


def rounded(draw, inset, radius, fill):
    draw.rounded_rectangle([inset, inset, S - 1 - inset, S - 1 - inset], radius=radius, fill=fill)


def tile():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    rounded(d, 32, 184, ACCENT)

    gradient = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(gradient)
    for y in range(S):
        t = y / (S - 1)
        gd.line([(0, y), (S, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(TILE_TOP, TILE_BOTTOM)) + (255,))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([64, 64, S - 65, S - 65], radius=152, fill=255)
    img.paste(gradient, (0, 0), mask)
    return img


def arc_arrow(d, cx, cy, r, start, end, width, color, head):
    """An arc from start to end degrees (clockwise in screen space) with an arrowhead at the end."""
    d.arc([cx - r, cy - r, cx + r, cy + r], start=start, end=end, fill=color, width=width)
    a = math.radians(end)
    tip = (cx + r * math.cos(a), cy + r * math.sin(a))
    tangent = (-math.sin(a), math.cos(a))          # clockwise direction of travel
    normal = (math.cos(a), math.sin(a))
    base = (tip[0] - tangent[0] * head * 0.2, tip[1] - tangent[1] * head * 0.2)
    point = (tip[0] + tangent[0] * head, tip[1] + tangent[1] * head)
    left = (base[0] + normal[0] * head * 0.85, base[1] + normal[1] * head * 0.85)
    right = (base[0] - normal[0] * head * 0.85, base[1] - normal[1] * head * 0.85)
    d.polygon([point, left, right], fill=color)


def cube(d, cx, cy, e):
    """An isometric cube of edge e centred on (cx, cy): light top, accent sides."""
    h = e * math.sqrt(3) / 2
    top = [(cx, cy - e), (cx + h, cy - e / 2), (cx, cy), (cx - h, cy - e / 2)]
    left = [(cx - h, cy - e / 2), (cx, cy), (cx, cy + e), (cx - h, cy + e / 2)]
    right = [(cx, cy), (cx + h, cy - e / 2), (cx + h, cy + e / 2), (cx, cy + e)]
    d.polygon(top, fill=WHITE)
    d.polygon(left, fill=ACCENT)
    d.polygon(right, fill=(20, 80, 180))


def main():
    img = tile()
    d = ImageDraw.Draw(img)
    cx, cy, r = S / 2, S / 2, 300
    arc_arrow(d, cx, cy, r, 200, 320, 56, STEEL, 92)   # top: left to right
    arc_arrow(d, cx, cy, r, 20, 140, 56, WHITE, 92)    # bottom: right to left
    cube(d, cx, cy, 170)

    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, "..", "assets")
    for size in (256, 120, 64, 32, 16):
        img.resize((size, size), Image.LANCZOS).save(os.path.join(out, "store_icon_%d.png" % size))
    print("assets/store_icon_{256,120,64,32,16}.png")


if __name__ == "__main__":
    main()
