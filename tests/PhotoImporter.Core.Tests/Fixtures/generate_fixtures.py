"""
Generates the media fixtures used by the integration tests.

Everything is synthetic: images are small pixel patterns, videos are minimal
MP4/MOV containers with metadata boxes and a pattern-filled 'mdat' (not playable).
Expected dates are asserted in FixtureMetadataTests.cs - keep them in sync.

Requires Pillow. Run from any directory:
  python generate_fixtures.py            all fixtures (media/ and formats/)
  python generate_fixtures.py formats    only formats/

formats/ holds structural replicas of real device files (Canon MP4/CR3, Pixel DNG/MP4): the same box and tag
layout as real files, but invented dates, places and names - no real photo, serial number or other private data.
"""
import io
import os
import shutil
import struct
import sys
from datetime import datetime, timezone

from PIL import Image

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "media")
FORMATS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "formats")


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


# ---------------------------------------------------------------- device-format replicas

CANON_UUID = bytes.fromhex("85c0b687820f11e08111f4ce462b6a48")


def tiff(entries):
    """Minimal little-endian TIFF with one IFD of ASCII tags (the layout Canon uses for CMT1/CMT2)."""
    entries = sorted(entries)
    data_offset = 8 + 2 + len(entries) * 12 + 4
    ifd, data = b"", b""
    for tag, text in entries:
        raw = text.encode("ascii") + b"\0"
        if len(raw) <= 4:
            value = raw.ljust(4, b"\0")
        else:
            value = struct.pack("<I", data_offset + len(data))
            data += raw + (b"\0" if len(raw) % 2 else b"")
        ifd += struct.pack("<HHI", tag, 2, len(raw)) + value
    return b"II*\0" + struct.pack("<I", 8) + struct.pack("<H", len(entries)) + ifd + struct.pack("<I", 0) + data


def exif_jpeg(index, original, offset, size=(160, 120)):
    """A small JPEG (pattern) whose EXIF has a capture time and the camera's UTC offset."""
    exif = Image.Exif()
    exif[0x0132] = original
    exif[0x8769] = {0x9003: original, 0x9004: original, 0x9010: offset, 0x9011: offset, 0x9012: offset}
    out = io.BytesIO()
    pattern(index, size).save(out, "JPEG", quality=80, exif=exif.tobytes())
    return out.getvalue()


def build_formats():
    shutil.rmtree(FORMATS, ignore_errors=True)
    os.makedirs(FORMATS)
    utc = timezone.utc

    # Canon MP4: moov/uuid(Canon)/CNTH/CNDA = JPEG thumbnail with EXIF on the camera's clock (+01:00, no DST);
    # mvhd holds UTC. 23:40 camera time vs 22:40 UTC: UTC + summer time would wrongly give the next day.
    canon_video = box("uuid", CANON_UUID,
                      box("CNCV", b"TestCodec/00.00.00/00.00.00"),
                      box("CNTH", box("CNDA", exif_jpeg(21, "2026:07:14 23:40:00", "+01:00"))))
    with open(os.path.join(FORMATS, "VID_canon.MP4"), "wb") as f:
        f.write(box("ftyp", b"mp42", b"\0\0\0\1", b"mp42", b"isom")
                + box("moov", canon_video, mvhd(datetime(2026, 7, 14, 22, 40, tzinfo=utc)))
                + mdat(21))

    # Canon CR3: ISO-BMFF with ftyp "crx "; moov/uuid(Canon) holds CMT1 (IFD0) and CMT2 (EXIF) as TIFF blocks.
    cr3_uuid = box("uuid", CANON_UUID,
                   box("CNCV", b"TestCodec/00.00.00/00.00.00"),
                   box("CMT1", tiff([(0x010F, "TestCam"), (0x0110, "TestCam R"), (0x0132, "2026:03:07 23:50:00")])),
                   box("CMT2", tiff([(0x9003, "2026:03:07 23:50:00"), (0x9004, "2026:03:07 23:50:00"),
                                     (0x9010, "+01:00"), (0x9011, "+01:00")])))
    with open(os.path.join(FORMATS, "IMG_canon.CR3"), "wb") as f:
        f.write(box("ftyp", b"crx ", b"\0\0\0\1", b"crx ", b"isom")
                + box("moov", cr3_uuid, mvhd(datetime(2026, 3, 7, 22, 50, tzinfo=utc)))
                + mdat(22))

    # Pixel-style DNG: a TIFF file with EXIF.
    exif = Image.Exif()
    exif[0x010F] = "TestPhone"
    exif[0x8769] = {0x9003: "2026:05:03 06:10:00", 0x9004: "2026:05:03 06:10:00"}
    pattern(23).save(os.path.join(FORMATS, "IMG_pixel.dng"), format="TIFF", exif=exif.tobytes())

    # Pixel-style MP4: mdat before moov, GPS in udta/(c)xyz, Android keys in meta (to be ignored), mvhd in UTC.
    # 22:30 UTC in Berlin (summer) is 00:30 the next day.
    android_keys = box("keys", b"\0\0\0\0", struct.pack(">I", 2),
                       box("mdta", b"com.android.version"), box("mdta", b"com.android.capture.fps"))
    android_ilst = box("ilst",
                       box(struct.pack(">I", 1).decode("latin1"), box("data", struct.pack(">II", 1, 0), b"14")),
                       box(struct.pack(">I", 2).decode("latin1"), box("data", struct.pack(">II", 23, 0), struct.pack(">f", 30.0))))
    with open(os.path.join(FORMATS, "VID_pixel.mp4"), "wb") as f:
        f.write(box("ftyp", b"isom", b"\0\0\0\0", b"isom", b"mp42") + mdat(24)
                + box("moov", mvhd(datetime(2026, 5, 2, 22, 30, tzinfo=utc)), xyz("+52.5200+013.4050/"),
                      box("meta", b"\0\0\0\0", box("hdlr", b"\0" * 25), android_keys, android_ilst)))

    for name in sorted(os.listdir(FORMATS)):
        print(f"formats/{name:26} {os.path.getsize(os.path.join(FORMATS, name)):6} bytes")


# ---------------------------------------------------------------- build

def main():
    if sys.argv[1:] == ["formats"]:
        build_formats()
        return
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

    build_formats()


if __name__ == "__main__":
    main()
