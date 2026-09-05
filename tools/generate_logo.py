#!/usr/bin/env python3
"""Generate LocalBridge app icons (PNG + ICO) using Pillow.

Run from the repository root:

    python3 tools/generate_logo.py

Outputs:
    LocalBridge/Assets/logo.png   512x512 master (RGBA, rounded corners)
    LocalBridge/Assets/logo.ico   multi-size Windows icon (16..256 px)
    LocalBridge.Android/Icon.png  256x256 Android launcher icon (padded)

The glyph is a white bridge (two pillars + arch) on a Catppuccin Macchiato
blue->mauve gradient — matching the app's theme.
"""

import os
from math import cos, pi, sin

from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Catppuccin Macchiato palette
BLUE = (137, 180, 250)
MAUVE = (203, 166, 247)
WHITE = (255, 255, 255)

CORNER_RATIO = 0.22   # rounded-square corner radius, relative to size
STROKE_RATIO = 0.088  # bridge stroke width, relative to size


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(size, c1, c2):
    """Diagonal gradient image (top-left -> bottom-right)."""
    img = Image.new("RGB", (size, size))
    px = img.load()
    denom = 2 * (size - 1)
    for y in range(size):
        for x in range(size):
            px[x, y] = lerp(c1, c2, (x + y) / denom)
    return img


def rounded_rect(size, corner):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, size - 1, size - 1], radius=corner, fill=255)
    return mask


def stamp_circle(draw, x, y, radius):
    draw.ellipse([x - radius, y - radius, x + radius, y + radius], fill=WHITE)


def draw_glyph(size):
    """White bridge glyph on a transparent square canvas."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    stroke = size * STROKE_RATIO
    r = stroke / 2
    step = max(2, int(stroke / 3))

    # Pillars
    x0, x1 = 0.27 * size, 0.73 * size
    yt, yb = 0.50 * size, 0.78 * size

    def pillar(x):
        y = yt
        while y <= yb:
            stamp_circle(draw, x, y, r)
            y += step

    # Arch (semicircle over the top)
    cx = size / 2
    arch_r = x1 - cx
    theta = pi
    d_theta = step / arch_r
    while theta >= 0:
        stamp_circle(draw, cx + arch_r * cos(theta), yt - arch_r * sin(theta), r)
        theta -= d_theta

    pillar(x0)
    pillar(x1)
    return img


def render(size, scale):
    """Full logo: rounded gradient square with the bridge glyph.

    `scale` shrinks the content around the center (used for the padded
    Android launcher icon).
    """
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    side = int(size * scale)
    offset = (size - side) // 2

    bg = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    bg.paste(gradient(side, BLUE, MAUVE),
             (0, 0), rounded_rect(side, int(side * CORNER_RATIO)))
    img.paste(bg, (offset, offset))

    glyph = draw_glyph(side)
    img.alpha_composite(glyph, (offset, offset))
    return img


def main():
    assets = os.path.join(ROOT, "LocalBridge", "Assets")
    android = os.path.join(ROOT, "LocalBridge.Android")
    os.makedirs(assets, exist_ok=True)

    master = render(512, 1.0)
    master.save(os.path.join(assets, "logo.png"))

    master.save(os.path.join(assets, "logo.ico"), format="ICO", sizes=[
        (16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256),
    ])

    launcher = render(256, 0.74)
    launcher.save(os.path.join(android, "Icon.png"))

    print("Generated:")
    print("  LocalBridge/Assets/logo.png")
    print("  LocalBridge/Assets/logo.ico")
    print("  LocalBridge.Android/Icon.png")


if __name__ == "__main__":
    main()