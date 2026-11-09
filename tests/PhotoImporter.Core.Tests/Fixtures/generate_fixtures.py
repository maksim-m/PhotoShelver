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


class Tiff:
    """TIFF writer with SHORT/LONG/ASCII tags and sub-IFD pointers, both byte orders (as in NEF, ARW, CR3 CMT1)."""

    def __init__(self, order="<"):
        self.o = order
        self.buf = bytearray((b"II" if order == "<" else b"MM") + struct.pack(order + "H", 42) + b"\0\0\0\0")

    def blob(self, data):
        if len(self.buf) % 2:
            self.buf += b"\0"
        offset = len(self.buf)
        self.buf += data
        return offset

    def ifd(self, entries, next_ifd=0):
        """entries: (tag, type, value) with type 2 = ASCII str, 3 = SHORT, 4 = LONG (int or list)."""
        encoded = []
        for tag, typ, value in sorted(entries, key=lambda e: e[0]):
            if typ == 2:
                raw = value.encode("ascii") + b"\0"
                count = len(raw)
            else:
                values = value if isinstance(value, list) else [value]
                raw = b"".join(struct.pack(self.o + ("H" if typ == 3 else "I"), v) for v in values)
                count = len(values)
            field = struct.pack(self.o + "I", self.blob(raw)) if len(raw) > 4 else raw.ljust(4, b"\0")
            encoded.append(struct.pack(self.o + "HHI", tag, typ, count) + field)
        if len(self.buf) % 2:
            self.buf += b"\0"
        offset = len(self.buf)
        self.buf += struct.pack(self.o + "H", len(encoded)) + b"".join(encoded) + struct.pack(self.o + "I", next_ifd)
        return offset

    def finish(self, first_ifd):
        self.buf[4:8] = struct.pack(self.o + "I", first_ifd)
        return bytes(self.buf)


def plain_jpeg(index, size):
    out = io.BytesIO()
    pattern(index, size).save(out, "JPEG", quality=80)
    return out.getvalue()


def lossless_jpeg_like(width, height):
    """Starts like a JPEG but is lossless (SOF3) - how CR2/NEF store RAW data. Must never be taken for a preview."""
    sof3 = b"\xff\xc3" + struct.pack(">HBHHB", 11, 12, height, width, 1) + b"\x01\x11\x00"
    return b"\xff\xd8" + sof3 + bytes((i * 13) % 256 for i in range(3000)) + b"\xff\xd9"


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

    # Canon CR3: ISO-BMFF with ftyp "crx "; moov/uuid(Canon) holds CMT1 (IFD0, orientation 8) and CMT2 (EXIF)
    # as TIFF blocks and THMB (160x120 thumbnail); a top-level uuid holds PRVW (the bigger preview, 400x300).
    cmt1 = Tiff("<")
    cmt1_data = cmt1.finish(cmt1.ifd([(0x010F, 2, "TestCam"), (0x0110, 2, "TestCam R"), (0x0112, 3, 8),
                                      (0x0132, 2, "2026:03:07 23:50:00")]))
    thmb = struct.pack(">BBHHHII", 0, 0, 0, 160, 120, len(plain_jpeg(25, (160, 120))), 0) + plain_jpeg(25, (160, 120))
    cr3_uuid = box("uuid", CANON_UUID,
                   box("CNCV", b"TestCodec/00.00.00/00.00.00"),
                   box("CMT1", cmt1_data),
                   box("CMT2", tiff([(0x9003, "2026:03:07 23:50:00"), (0x9004, "2026:03:07 23:50:00"),
                                     (0x9010, "+01:00"), (0x9011, "+01:00")])),
                   box("THMB", thmb))
    prvw_jpeg = plain_jpeg(26, (400, 300))
    prvw = box("uuid", bytes.fromhex("eaf42b5e1c984b88b9fbb7dc406e4d16"), b"\0" * 8,
               box("PRVW", struct.pack(">IHHHHI", 0, 1, 400, 300, 1, len(prvw_jpeg)) + prvw_jpeg))
    with open(os.path.join(FORMATS, "IMG_canon.CR3"), "wb") as f:
        f.write(box("ftyp", b"crx ", b"\0\0\0\1", b"crx ", b"isom")
                + box("moov", cr3_uuid, mvhd(datetime(2026, 3, 7, 22, 50, tzinfo=utc)))
                + prvw + mdat(22))

    # Nikon-style NEF: big-endian TIFF; preview JPEG (400x300) in a sub-IFD, RAW data in another sub-IFD stored
    # as lossless JPEG (must not be picked), a 160x120 thumbnail in IFD1, orientation 6, date in the EXIF IFD.
    t = Tiff(">")
    preview, thumb, raw = plain_jpeg(27, (400, 300)), plain_jpeg(28, (160, 120)), lossless_jpeg_like(360, 240)
    p_off, th_off, raw_off = t.blob(preview), t.blob(thumb), t.blob(raw)
    exif_ifd = t.ifd([(0x9003, 2, "2026:08:20 23:55:00"), (0x9004, 2, "2026:08:20 23:55:00")])
    sub_preview = t.ifd([(0x00FE, 4, 1), (0x0103, 3, 6), (0x0201, 4, p_off), (0x0202, 4, len(preview))])
    sub_raw = t.ifd([(0x00FE, 4, 0), (0x0103, 3, 6), (0x0111, 4, raw_off), (0x0117, 4, len(raw))])
    ifd1 = t.ifd([(0x0103, 3, 6), (0x0201, 4, th_off), (0x0202, 4, len(thumb))])
    ifd0 = t.ifd([(0x010F, 2, "TestCam"), (0x0110, 2, "TestCam N"), (0x0112, 3, 6), (0x0132, 2, "2026:08:20 23:55:00"),
                  (0x014A, 4, [sub_preview, sub_raw]), (0x8769, 4, exif_ifd)], next_ifd=ifd1)
    with open(os.path.join(FORMATS, "IMG_nikon.NEF"), "wb") as f:
        f.write(t.finish(ifd0))

    # Sony-style ARW: little-endian TIFF; preview JPEG (400x300) referenced from IFD0 itself, thumbnail in IFD1,
    # orientation 8, date in the EXIF IFD.
    t = Tiff("<")
    preview, thumb = plain_jpeg(29, (400, 300)), plain_jpeg(30, (160, 120))
    p_off, th_off = t.blob(preview), t.blob(thumb)
    exif_ifd = t.ifd([(0x9003, 2, "2026:09:01 00:05:00"), (0x9004, 2, "2026:09:01 00:05:00")])
    ifd1 = t.ifd([(0x0103, 3, 6), (0x0201, 4, th_off), (0x0202, 4, len(thumb))])
    ifd0 = t.ifd([(0x010F, 2, "TestCam"), (0x0110, 2, "TestCam S"), (0x0112, 3, 8), (0x0132, 2, "2026:09:01 00:05:00"),
                  (0x0201, 4, p_off), (0x0202, 4, len(preview)), (0x8769, 4, exif_ifd)], next_ifd=ifd1)
    with open(os.path.join(FORMATS, "IMG_sony.ARW"), "wb") as f:
        f.write(t.finish(ifd0))

    # Fujifilm-style RAF: "FUJIFILMCCD-RAW" header; offset/length of an embedded JPEG at bytes 84/88; the JPEG's
    # EXIF has the date and orientation 3.
    exif = Image.Exif()
    exif[0x010F] = "TestCam"
    exif[0x0112] = 3
    exif[0x8769] = {0x9003: "2026:10:10 10:10:00", 0x9004: "2026:10:10 10:10:00"}
    out = io.BytesIO()
    pattern(31, (400, 300)).save(out, "JPEG", quality=80, exif=exif.tobytes())
    raf_jpeg = out.getvalue()
    header = bytearray(b"FUJIFILMCCD-RAW 0201FF000000" + b"TestCam".ljust(32, b"\0"))
    header = header.ljust(160, b"\0")
    header[84:92] = struct.pack(">II", 160, len(raf_jpeg))
    with open(os.path.join(FORMATS, "IMG_fuji.RAF"), "wb") as f:
        f.write(bytes(header) + raf_jpeg + bytes((i * 7) % 256 for i in range(4096)))

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
