"""
Generates the media fixtures used by the integration tests.

Everything is synthetic: images are small pixel patterns, videos are minimal
MP4/MOV containers with metadata boxes and a pattern-filled 'mdat' (not playable).
Expected dates are asserted in FixtureMetadataTests.cs - keep them in sync.

Requires Pillow. Run from any directory:  python generate_fixtures.py
"""
import os
import shutil
import struct
from datetime import datetime, timezone

from PIL import Image

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "media")


# ---------------------------------------------------------------- images

def pattern(index, size=(64, 48)):
    """Checkerboard in a per-file colour plus a row of bits encoding the index (unique content)."""
    w, h = size
    hue = [(255, 80, 80), (80, 200, 80), (80, 120, 255), (230, 200, 60), (200, 90, 220), (60, 210, 210), (240, 140, 60), (150, 150, 150)][index % 8]
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(h):
        for x in range(w):
            on = ((x // 8) + (y // 8)) % 2 == 0
            px[x, y] = hue if on else tuple(c // 3 for c in hue)
    for bit in range(16):
        colour = (255, 255, 255) if (index >> bit) & 1 else (0, 0, 0)
        for y in range(h - 4, h):
            for x in range(bit * 4, bit * 4 + 4):
                px[x, y] = colour
    return img


def save_jpeg(path, index, original=None, digitized=None, ifd0=None, offset=None):
    exif = Image.Exif()
    sub = {}
    if original: sub[0x9003] = original
    if digitized: sub[0x9004] = digitized
    if offset: sub[0x9011] = offset  # OffsetTimeOriginal
    if sub: exif[0x8769] = sub
    if ifd0: exif[0x0132] = ifd0
    kwargs = {"exif": exif.tobytes()} if (sub or ifd0) else {}
    pattern(index).save(path, "JPEG", quality=90, **kwargs)


# ---------------------------------------------------------------- ISO-BMFF

def box(kind, *payload):
    body = b"".join(payload)
    return struct.pack(">I", 8 + len(body)) + kind.encode("latin1") + body


def large_box(kind, *payload):
    """Box using the 64-bit 'largesize' header."""
    body = b"".join(payload)
    return struct.pack(">I", 1) + kind.encode("latin1") + struct.pack(">Q", 16 + len(body)) + body


def secs_1904(dt):
    return int((dt - datetime(1904, 1, 1, tzinfo=timezone.utc)).total_seconds())


def mvhd(dt, version=0):
    if dt is None:
        return box("mvhd", b"\0\0\0\0", struct.pack(">II", 0, 0), b"\0" * 88)
    s = secs_1904(dt)
    if version == 1:
        return box("mvhd", b"\1\0\0\0", struct.pack(">QQ", s, s), b"\0" * 92)
    return box("mvhd", b"\0\0\0\0", struct.pack(">II", s, s), b"\0" * 88)


def apple_meta(items, iso_full_box=False):
    keys = box("keys", b"\0\0\0\0", struct.pack(">I", len(items)), *[box("mdta", k.encode()) for k, _ in items])
    ilst = box("ilst", *[
        box(struct.pack(">I", i + 1).decode("latin1"), box("data", struct.pack(">II", 1, 0), v.encode()))
        for i, (_, v) in enumerate(items)])
    prefix = b"\0\0\0\0" if iso_full_box else b""
    return box("meta", prefix, box("hdlr", b"\0" * 25), keys, ilst)


def xyz(location):
    data = location.encode()
    return box("udta", box("©xyz", struct.pack(">HH", len(data), 0x15C7), data))


def mdat(index, size=4096):
    return box("mdat", bytes((i * 7 + index * 31) % 256 for i in range(size)))


def save_video(path, index, moov_children, brand=b"isom", large_moov=False):
    moov = large_box("moov", *moov_children) if large_moov else box("moov", *moov_children)
    with open(path, "wb") as f:
        f.write(box("ftyp", brand, b"\0\0\0\0", brand) + moov + mdat(index))


# ---------------------------------------------------------------- build

def main():
    shutil.rmtree(ROOT, ignore_errors=True)
    photos = os.path.join(ROOT, "photos")
    video = os.path.join(ROOT, "video")
    other = os.path.join(ROOT, "other")
    for d in (photos, os.path.join(photos, "sub"), video, other):
        os.makedirs(d)

    save_jpeg(os.path.join(photos, "IMG_0001.JPG"), 1, original="2026:11:05 09:15:00")
    save_jpeg(os.path.join(photos, "IMG_0002.JPG"), 2, original="2026:11:05 23:59:59", offset="+09:00")
    save_jpeg(os.path.join(photos, "IMG_0003.JPG"), 3, digitized="2026:11:07 10:00:00")
    save_jpeg(os.path.join(photos, "IMG_0004.JPG"), 4, ifd0="2026:11:07 11:00:00")
    save_jpeg(os.path.join(photos, "IMG_0005.JPG"), 5)  # no EXIF
    save_jpeg(os.path.join(photos, "img_0006.jpeg"), 6, original="2026:11:08 00:00:01")
    pattern(7).save(os.path.join(photos, "IMG_0007.PNG"))  # no metadata
    shutil.copyfile(os.path.join(photos, "IMG_0001.JPG"), os.path.join(photos, "sub", "IMG_0001_copy.JPG"))
    with open(os.path.join(photos, "BROKEN.JPG"), "wb") as f:
        f.write(b"\xff\xd8\xff\xe1\x00\x10Exif\x00\x00" + bytes(range(200)))
    open(os.path.join(photos, "EMPTY.JPG"), "wb").close()

    utc = timezone.utc
    save_video(os.path.join(video, "VID_apple.MOV"), 1, [
        mvhd(datetime(2026, 11, 7, 14, 30, tzinfo=utc)),
        apple_meta([("com.apple.quicktime.location.ISO6709", "+35.6812+139.7671+040.000/"),
                    ("com.apple.quicktime.creationdate", "2026-11-07T23:30:00+0900")]),
    ], brand=b"qt  ")
    save_video(os.path.join(video, "VID_gps.MP4"), 2, [
        mvhd(datetime(2026, 11, 7, 22, 30, tzinfo=utc)),
        xyz("+35.6812+139.7671/"),
    ])
    save_video(os.path.join(video, "VID_utc.MP4"), 3, [mvhd(datetime(2026, 1, 15, 23, 30, tzinfo=utc))])
    save_video(os.path.join(video, "VID_v1.MP4"), 4, [mvhd(datetime(2026, 6, 1, 10, 0, tzinfo=utc), version=1)], large_moov=True)
    save_video(os.path.join(video, "VID_isometa.MP4"), 5, [
        mvhd(datetime(2026, 3, 1, 13, 0, tzinfo=utc)),
        apple_meta([("com.apple.quicktime.creationdate", "2026-03-01T08:00:00-0500")], iso_full_box=True),
    ])
    save_video(os.path.join(video, "VID_nodate.MP4"), 6, [mvhd(None)])
    with open(os.path.join(video, "VID_truncated.MP4"), "wb") as f:
        f.write(box("ftyp", b"isom", b"\0\0\0\0") + struct.pack(">I", 100000) + b"moov" + mvhd(datetime(2026, 2, 2, tzinfo=utc)))

    with open(os.path.join(other, "notes.txt"), "w") as f:
        f.write("not media\n")
    with open(os.path.join(other, "IMG_0001.XMP"), "w") as f:
        f.write("<x:xmpmeta/>\n")

    for dirpath, _, files in os.walk(ROOT):
        for name in sorted(files):
            p = os.path.join(dirpath, name)
            print(f"{os.path.relpath(p, ROOT):32} {os.path.getsize(p):6} bytes")


if __name__ == "__main__":
    main()
