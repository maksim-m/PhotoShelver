"""
Draws the application icon: a photo (mountains + sun) on a blue Windows-11-style tile with an orange
"import" badge (down arrow). Writes src/PhotoShelver.App/Assets/app.ico with 16-256 px images.
Small sizes use a simplified drawing so they stay readable in the taskbar and title bar.

Requires Pillow:  python tools/make_icon.py [preview.png]
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFilter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(REPO, "src", "PhotoShelver.App", "Assets", "app.ico")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

BLUE_TOP = (59, 130, 246)
BLUE_BOTTOM = (29, 78, 216)
SKY = (191, 219, 254)
MOUNTAIN_BACK = (34, 197, 94)
MOUNTAIN_FRONT = (21, 128, 61)
SUN = (250, 204, 21)
BADGE = (249, 115, 22)
WHITE = (255, 255, 255)


def draw(size, simple):
    """Draws at 4x and downsamples for smooth edges."""
    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    def px(v):
        return round(v * s)

    # Tile with a vertical gradient.
    tile = Image.new("RGBA", (s, s))
    for y in range(s):
        t = y / (s - 1)
        tile.paste(tuple(round(a + (b - a) * t) for a, b in zip(BLUE_TOP, BLUE_BOTTOM)) + (255,), (0, y, s, y + 1))
    mask = Image.new("L", (s, s), 0)
    margin = 0.02 if simple else 0.04
    ImageDraw.Draw(mask).rounded_rectangle([px(margin), px(margin), px(1 - margin), px(1 - margin)], radius=px(0.22), fill=255)
    img.paste(tile, (0, 0), mask)

    d = ImageDraw.Draw(img)

    # Photo card (with a soft shadow on large sizes).
    card = (0.15, 0.18, 0.80, 0.72) if not simple else (0.12, 0.16, 0.84, 0.74)
    if not simple:
        shadow = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        ImageDraw.Draw(shadow).rounded_rectangle(
            [px(card[0]), px(card[1] + 0.02), px(card[2]), px(card[3] + 0.02)], radius=px(0.05), fill=(0, 0, 40, 90))
        img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(px(0.02))))
        d = ImageDraw.Draw(img)
    d.rounded_rectangle([px(card[0]), px(card[1]), px(card[2]), px(card[3])], radius=px(0.05 if not simple else 0.06), fill=WHITE)

    # Picture inside the card.
    inset = 0.045 if not simple else 0.06
    pic = (card[0] + inset, card[1] + inset, card[2] - inset, card[3] - inset)
    picture = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    pd = ImageDraw.Draw(picture)
    pd.rectangle([px(pic[0]), px(pic[1]), px(pic[2]), px(pic[3])], fill=SKY)
    w, h = pic[2] - pic[0], pic[3] - pic[1]
    if not simple:
        pd.ellipse([px(pic[0] + w * 0.66), px(pic[1] + h * 0.14), px(pic[0] + w * 0.86), px(pic[1] + h * 0.14 + w * 0.20)], fill=SUN)
        pd.polygon([(px(pic[0] + w * 0.30), px(pic[3])), (px(pic[0] + w * 0.62), px(pic[1] + h * 0.38)), (px(pic[2] + w * 0.05), px(pic[3]))], fill=MOUNTAIN_BACK)
    pd.polygon([(px(pic[0] - w * 0.05), px(pic[3])), (px(pic[0] + w * 0.32), px(pic[1] + h * (0.22 if not simple else 0.15))), (px(pic[0] + w * 0.72), px(pic[3]))], fill=MOUNTAIN_FRONT)
    clip = Image.new("L", (s, s), 0)
    ImageDraw.Draw(clip).rectangle([px(pic[0]), px(pic[1]), px(pic[2]), px(pic[3])], fill=255)
    img.paste(picture, (0, 0), Image.composite(picture.getchannel("A"), Image.new("L", (s, s), 0), clip))
    d = ImageDraw.Draw(img)

    # Import badge: white ring, orange disc, white down arrow.
    cx, cy, r = (0.74, 0.74, 0.21) if not simple else (0.72, 0.72, 0.26)
    ring = 0.035 if not simple else 0.05
    d.ellipse([px(cx - r - ring), px(cy - r - ring), px(cx + r + ring), px(cy + r + ring)], fill=WHITE)
    d.ellipse([px(cx - r), px(cy - r), px(cx + r), px(cy + r)], fill=BADGE)
    shaft = r * (0.22 if not simple else 0.30)
    d.rectangle([px(cx - shaft / 2), px(cy - r * 0.55), px(cx + shaft / 2), px(cy + r * 0.05)], fill=WHITE)
    head = r * (0.62 if not simple else 0.72)
    d.polygon([(px(cx - head), px(cy - r * 0.02)), (px(cx + head), px(cy - r * 0.02)), (px(cx), px(cy + r * 0.60))], fill=WHITE)

    return img.resize((size, size), Image.LANCZOS)


def main():
    images = [draw(size, simple=size <= 24) for size in SIZES]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    images[-1].save(OUT, format="ICO", sizes=[(i.width, i.height) for i in images], append_images=images[:-1])
    print(f"written {OUT}")

    if len(sys.argv) > 1:  # preview strip on white and on dark backgrounds
        strip = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES) + 10, 2 * 256 + 30), (255, 255, 255, 255))
        ImageDraw.Draw(strip).rectangle([0, 256 + 15, strip.width, strip.height], fill=(32, 32, 32, 255))
        x = 10
        for img in images:
            strip.alpha_composite(img, (x, 256 - img.height))
            strip.alpha_composite(img, (x, 256 + 20))
            x += img.width + 10
        strip.save(sys.argv[1])
        print(f"preview {sys.argv[1]}")


if __name__ == "__main__":
    main()
