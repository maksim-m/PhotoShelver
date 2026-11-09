"""
Builds a demo "memory card" and photo library for the README screenshot: procedurally drawn landscapes
(no real photos), EXIF capture dates over a few days, a target library with some earlier imports and
two files that are already in it (shown as duplicates).

    python tools/make_demo_library.py <empty folder>

Creates <folder>/card and <folder>/library. Needs Pillow.
"""
import io
import math
import os
import random
import shutil
import sys

from PIL import Image, ImageDraw, ImageFilter

W, H = 4000, 2667


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(draw, top, bottom, y0, y1, width):
    for y in range(y0, y1):
        draw.line([(0, y), (width, y)], fill=lerp(top, bottom, (y - y0) / max(1, y1 - y0 - 1)))


def ridge(rng, width, base, amplitude, peaks):
    """Mountain silhouette: a few peaks (tent shapes of random height and width) plus small jagged noise."""
    tops = [(rng.uniform(-0.1, 1.1) * width, rng.uniform(0.35, 1.0) * amplitude, rng.uniform(0.12, 0.3) * width) for _ in range(peaks)]
    points, jag = [], 0.0
    for x in range(0, width + 8, 8):
        lift = max([height * max(0.0, 1 - abs(x - px) / half) for px, height, half in tops] + [0.0])
        jag = jag * 0.7 + rng.uniform(-1, 1) * amplitude * 0.05
        points.append((x, base - lift + jag))
    return points


SCENES = {
    "sunset": dict(sky=((255, 140, 90), (255, 214, 150)), sun=(255, 240, 200), layers=[(120, 70, 90), (80, 45, 70), (45, 28, 50)], water=None),
    "alpine": dict(sky=((110, 170, 230), (210, 232, 250)), sun=None, layers=[(150, 160, 180), (110, 135, 160), (50, 90, 70)], water=None, snow=True, amp=0.34),
    "lake": dict(sky=((70, 130, 200), (190, 220, 240)), sun=(255, 250, 230), layers=[(120, 150, 170), (60, 100, 90)], water=(60, 110, 150)),
    "dusk": dict(sky=((40, 50, 110), (230, 120, 110)), sun=(255, 200, 150), layers=[(70, 60, 100), (40, 35, 70), (20, 20, 40)], water=None),
    "sea": dict(sky=((90, 170, 230), (225, 240, 250)), sun=(255, 255, 235), layers=[], water=(30, 120, 170), beach=True),
    "forest": dict(sky=((150, 190, 220), (230, 236, 230)), sun=None, layers=[(110, 140, 120), (60, 100, 70), (30, 70, 45)], water=None, trees=True),
}


def scene(kind, seed, size=(W, H)):
    rng = random.Random(seed)
    s = SCENES[kind]
    w, h = size
    img = Image.new("RGB", size)
    d = ImageDraw.Draw(img)
    horizon = int(h * rng.uniform(0.55, 0.68))
    gradient(d, s["sky"][0], s["sky"][1], 0, horizon + 40, w)
    if s["sun"]:
        cx, cy, r = rng.uniform(0.2, 0.8) * w, horizon - rng.uniform(0.08, 0.3) * h, h * 0.06
        glow = Image.new("L", size, 0)
        ImageDraw.Draw(glow).ellipse([cx - r * 3, cy - r * 3, cx + r * 3, cy + r * 3], fill=110)
        img.paste(s["sun"], mask=glow.filter(ImageFilter.GaussianBlur(r * 1.5)))
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=s["sun"])
    for i, color in enumerate(s["layers"]):
        base = horizon - h * (0.12 - i * 0.05)
        amp = s.get("amp", 0.2) if i == 0 else 0.16 - i * 0.03
        pts = ridge(rng, w, base, h * amp, 4 if i == 0 else 6)
        d.polygon(pts + [(w, h), (0, h)], fill=color)
        if s.get("snow") and i == 0:
            # Snow caps: between the ridge and the snow line, only where the ridge is above it.
            snow_line = min(y for _, y in pts) + h * 0.09
            lower = [(x, max(y, snow_line)) for x, y in pts]
            d.polygon(pts + lower[::-1], fill=(245, 248, 252))
    if s.get("trees"):
        for _ in range(140):
            x, base = rng.uniform(0, w), rng.uniform(horizon, h)
            th = (base - horizon + 40) * rng.uniform(0.25, 0.4)
            shade = lerp((25, 60, 40), (15, 40, 25), rng.random())
            d.polygon([(x, base - th), (x - th * 0.22, base), (x + th * 0.22, base)], fill=shade)
    if s["water"]:
        gradient(d, lerp(s["water"], (255, 255, 255), 0.25), s["water"], horizon, h, w)
        for _ in range(500):
            y = rng.uniform(horizon, h)
            x = rng.uniform(0, w)
            ln = (rng.uniform(10, 60) * (y - horizon) / (h - horizon) + 4) * w / 1600
            d.line([(x, y), (x + ln, y)], fill=lerp(s["water"], (255, 255, 255), 0.45), width=max(1, w // 800))
    if s.get("beach"):
        sand_top = int(h * 0.82)
        pts = [(x, sand_top + math.sin(x / (90 * w / 1600) + seed) * 10 * w / 1600) for x in range(0, w + 20, 20)]
        d.polygon(pts + [(w, h), (0, h)], fill=(232, 210, 165))
    # gentle grain so it looks less like a vector drawing
    noise = Image.effect_noise(size, 18).convert("RGB")
    return Image.blend(img, noise, 0.07)


def save(img, path, when):
    exif = Image.Exif()
    exif[0x010F] = "DemoCam"
    exif[0x0110] = "DemoCam 1"
    exif[0x8769] = {0x9003: when, 0x9004: when}
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path, "JPEG", quality=92, exif=exif.tobytes())


def main():
    root = sys.argv[1]
    if os.path.exists(root) and os.listdir(root):
        sys.exit(f"{root} is not empty")
    card = os.path.join(root, "card", "DCIM", "100DEMO")
    library = os.path.join(root, "library")

    shots = [
        # (day, time, scene)
        ("2026:08:14", "07:12:05", "alpine"), ("2026:08:14", "09:40:51", "alpine"), ("2026:08:14", "12:03:18", "lake"),
        ("2026:08:14", "14:27:44", "lake"), ("2026:08:14", "17:55:02", "forest"), ("2026:08:14", "20:31:39", "sunset"),
        ("2026:08:15", "08:05:13", "forest"), ("2026:08:15", "11:16:27", "lake"), ("2026:08:15", "19:48:56", "dusk"),
        ("2026:08:15", "20:02:10", "dusk"),
        ("2026:08:16", "10:22:31", "sea"), ("2026:08:16", "13:09:04", "sea"), ("2026:08:16", "16:45:20", "sea"),
        ("2026:08:16", "20:14:48", "sunset"),
    ]
    for i, (day, time, kind) in enumerate(shots):
        portrait = i in (4, 9)
        size = (H, W) if portrait else (W, H)
        save(scene(kind, 100 + i, size), os.path.join(card, f"IMG_{2041 + i:04d}.JPG"), f"{day} {time}")

    # Earlier imports in the library (folders only matter for the tree; a few files each).
    for day, kind, seed in [("2026-06-02", "lake", 1), ("2026-07-19", "sunset", 2), ("2026-07-20", "forest", 3), ("2025-12-31", "dusk", 4)]:
        y = day[:4]
        save(scene(kind, seed, (800, 533)), os.path.join(library, y, day, f"IMG_{seed:04d}.JPG"), day.replace("-", ":") + " 12:00:00")

    # Two files of the card were already imported last time: the duplicate check finds them.
    for name in ("IMG_2041.JPG", "IMG_2042.JPG"):
        dest = os.path.join(library, "2026", "2026-08-14")
        os.makedirs(dest, exist_ok=True)
        shutil.copy2(os.path.join(card, name), os.path.join(dest, name))

    print("card:", os.path.join(root, "card"))
    print("library:", library)


if __name__ == "__main__":
    main()
