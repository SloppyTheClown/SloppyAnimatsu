"""Draws the Sloppy Animatsu application icon.

The icon is a clown nose (a glossy red ball) inside a ring split into three arcs in the Spinner
window's axis colours: X red, Y green, Z blue (src/SloppyAnimatsu.Viewer/SpinnerDialView.cs AxisColors).
Run this file with Pillow installed to rewrite src/SloppyAnimatsu.Viewer/Assets/app.ico;
packaging/linux/sloppyanimatsu.svg is the same drawing by hand.

    python packaging/icon.py
"""

import pathlib

from PIL import Image, ImageDraw, ImageFilter

SIZE = 256
BACKGROUND = (20, 20, 22, 255)
BORDER = (60, 60, 66, 255)
AXES = [(242, 64, 64, 255), (77, 230, 77, 255), (77, 128, 255, 255)]
NOSE_DARK = (150, 10, 22, 255)
NOSE = (225, 30, 40, 255)
HIGHLIGHT = (255, 235, 235, 235)


def draw(scale: int) -> Image.Image:
    """One square at `scale` x SIZE pixels, drawn through a 4x supersampled canvas."""
    k = 4
    big = SIZE * k
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    pen = ImageDraw.Draw(image)
    pen.rounded_rectangle([0, 0, big - 1, big - 1], radius=big // 5, fill=BACKGROUND,
                          outline=BORDER, width=big // 80)
    c = big / 2
    # The spinner ring: three 110-degree arcs with gaps, X at the top right, then Y, then Z.
    r = 104 * k
    width = 26 * k
    for i, colour in enumerate(AXES):
        start = -90 + i * 120 + 5
        pen.arc([c - r, c - r, c + r, c + r], start, start + 110, fill=colour, width=width)
    # The nose: a dark rim, a lighter body offset up-left, and a soft highlight.
    n = 62 * k
    pen.ellipse([c - n, c - n, c + n, c + n], fill=NOSE_DARK)
    b = 56 * k
    pen.ellipse([c - b - 4 * k, c - b - 4 * k, c + b - 4 * k, c + b - 4 * k], fill=NOSE)
    glow = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    gp = ImageDraw.Draw(glow)
    hx, hy, hr = c - 24 * k, c - 26 * k, 15 * k
    gp.ellipse([hx - hr, hy - hr * 0.8, hx + hr, hy + hr * 0.8], fill=HIGHLIGHT)
    glow = glow.filter(ImageFilter.GaussianBlur(3 * k))
    image.alpha_composite(glow)
    return image.resize((scale, scale), Image.LANCZOS)


def main() -> None:
    root = pathlib.Path(__file__).resolve().parent.parent
    target = root / "src" / "SloppyAnimatsu.Viewer" / "Assets" / "app.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    # The largest image is the base; Pillow derives the smaller sizes from it for the .ico.
    draw(SIZE).save(target, format="ICO",
                    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(f"wrote {target}")


if __name__ == "__main__":
    main()
