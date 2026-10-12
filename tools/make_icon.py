#!/usr/bin/env python3
"""
HHL Gamepad Link - app icon generator.

Draws src/app.ico (16 to 256 px) and assets/icon-512.png. The artwork is the HHL Gamepad Config
icon (dark tile, lavender ring, console-gray pad, SFC buttons) with a link badge: two chain links
on the app's cyan accent, bottom right.

    python tools/make_icon.py

Requires Pillow (>= 10.1 for multi-image ICO). Shapes are drawn at 4x and downscaled.
"""
import os

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SS = 4          # supersampling factor
U = 1024        # design units

# ---- palette (matches the config app and the window's accent) -------------
RED, YELLOW, BLUE, GREEN = "#E23B3B", "#F4B41A", "#2F6BD8", "#23A35A"
LAVENDER = "#8E7CC3"
GRAY_L, GRAY_M, GRAY_D = "#E9E8EE", "#C9C7D3", "#6E6B7B"
BG_DARK, SURFACE_DARK = "#121117", "#1B1A22"
CYAN, WHITE = "#40C4D6", "#FFFFFF"

# ---- geometry (design units, from the config app's icon) ------------------
TILE_R = 230
RING_R, RING_W = 430, 16
PAD_R, PAD_BEVEL = 372, 20
WELL_SIDE, WELL_RX = 489, 130
BTN_OFF, BTN_R, BTN_SHADOW = 165, 98, 14
BUTTONS = [(0, -1, BLUE), (1, 0, RED), (0, 1, YELLOW), (-1, 0, GREEN)]

# Link badge: a cyan disc cut out of the art by a dark gap, two white chain links inside, at 45 degrees
BADGE_X, BADGE_Y, BADGE_R, BADGE_GAP = 790, 790, 200, 34
LINK_LEN, LINK_H, LINK_W, LINK_OFF = 190, 100, 30, 62  # one link's length, height, stroke, offset

# Small sizes: a bigger pad and buttons and a bigger badge, so they still read at 16 px
SMALL = dict(pad_r=430, btn_off=205, btn_r=120, badge_r=260, link_w=48)


def _rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4)) + (255,)


def draw(px, small):
    n = px * SS
    k = n / U
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    def disc(cx, cy, r, col):
        d.ellipse([(cx - r) * k, (cy - r) * k, (cx + r) * k, (cy + r) * k], fill=_rgb(col))

    # Tile: a vertical gradient through a rounded-square mask
    grad = Image.new("RGBA", (1, n))
    top, bot = _rgb(SURFACE_DARK), _rgb(BG_DARK)
    for y in range(n):
        t = y / (n - 1)
        grad.putpixel((0, y), tuple(round(a + (b - a) * t) for a, b in zip(top, bot)))
    grad = grad.resize((n, n))
    mask = Image.new("L", (n, n), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, n - 1, n - 1], radius=TILE_R * k, fill=255)
    img.paste(grad, mask=mask)

    c = U / 2
    if small:
        disc(c, c, SMALL["pad_r"], GRAY_L)
        for dx, dy, col in BUTTONS:
            disc(c + dx * SMALL["btn_off"], c + dy * SMALL["btn_off"], SMALL["btn_r"], col)
        badge_r, link_w = SMALL["badge_r"], SMALL["link_w"]
    else:
        ro = RING_R + RING_W / 2
        d.ellipse([(c - ro) * k, (c - ro) * k, (c + ro) * k, (c + ro) * k],
                  outline=_rgb(LAVENDER), width=round(RING_W * k))
        disc(c, c + PAD_BEVEL, PAD_R, GRAY_M)
        disc(c, c, PAD_R, GRAY_L)
        well = Image.new("L", (n, n), 0)
        h = WELL_SIDE / 2
        ImageDraw.Draw(well).rounded_rectangle([(c - h) * k, (c - h) * k, (c + h) * k, (c + h) * k],
                                               radius=WELL_RX * k, fill=255)
        well = well.rotate(45, resample=Image.BICUBIC, center=(c * k, c * k))
        img.paste(Image.new("RGBA", (n, n), _rgb(GRAY_M)), mask=well)
        for dx, dy, col in BUTTONS:
            bx, by = c + dx * BTN_OFF, c + dy * BTN_OFF
            disc(bx, by + BTN_SHADOW, BTN_R, GRAY_D)
            disc(bx, by, BTN_R, col)
        badge_r, link_w = BADGE_R, LINK_W

    # Link badge, separated from the art by a ring of the tile's colour
    bx, by = BADGE_X + (badge_r - BADGE_R) * 0.35, BADGE_Y + (badge_r - BADGE_R) * 0.35
    disc(bx, by, badge_r + BADGE_GAP, BG_DARK)
    disc(bx, by, badge_r, CYAN)
    # Two overlapping rounded links, drawn level on their own layer and turned 45 degrees
    s = badge_r / BADGE_R
    links = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    ld = ImageDraw.Draw(links)
    hl, hh = LINK_LEN * s / 2, LINK_H * s / 2
    for dx in (-LINK_OFF * s, LINK_OFF * s):
        cx = bx + dx
        ld.rounded_rectangle([(cx - hl) * k, (by - hh) * k, (cx + hl) * k, (by + hh) * k],
                             radius=hh * k, outline=_rgb(WHITE), width=round(link_w * k))
    links = links.rotate(45, resample=Image.BICUBIC, center=(bx * k, by * k))
    img.alpha_composite(links)

    return img.resize((px, px), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [draw(px, small=px <= 32) for px in sizes]
    ico = os.path.join(ROOT, "src", "app.ico")
    frames[-1].save(ico, format="ICO", sizes=[(px, px) for px in sizes], append_images=frames[:-1])
    print("wrote", os.path.relpath(ico, ROOT))

    os.makedirs(os.path.join(ROOT, "assets"), exist_ok=True)
    png = os.path.join(ROOT, "assets", "icon-512.png")
    draw(512, small=False).save(png, optimize=True)
    print("wrote", os.path.relpath(png, ROOT))


if __name__ == "__main__":
    main()
