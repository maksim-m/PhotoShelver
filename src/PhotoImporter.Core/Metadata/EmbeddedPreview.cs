using System.Buffers.Binary;
using System.Text;

namespace PhotoImporter.Core.Metadata;

/// <param name="Jpeg">The camera's JPEG preview embedded in the RAW file.</param>
/// <param name="Orientation">EXIF orientation of the shot (1 = upright, 3 = 180°, 6 = 90° clockwise, 8 = 90° counter-clockwise).</param>
public sealed record PreviewImage(byte[] Jpeg, int Orientation);

/// <summary>
/// Extracts the JPEG preview that cameras store inside RAW files - no RAW decoding, no codec needed, read only.
/// Supports TIFF-based RAW (CR2, NEF, ARW, DNG, ORF, RW2, PEF…), Canon CR3 and Fujifilm RAF.
/// Picks the smallest preview that is still at least <see cref="MinPreviewSide"/> pixels wide/high.
/// Never throws: returns null when nothing usable is found.
/// </summary>
public static class EmbeddedPreview
{
    public const int MinPreviewSide = 320;
    private const int MaxPreviewBytes = 32 * 1024 * 1024;
    private const int MaxIfds = 64;
    private static readonly Guid CanonUuid = new("85c0b687-820f-11e0-8111-f4ce462b6a48");
    private static readonly Guid CanonPreviewUuid = new("eaf42b5e-1c98-4b88-b9fb-b7dc406e4d16");

    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2",
    };

    public static bool IsRaw(string path) => RawExtensions.Contains(Path.GetExtension(path));

    public static PreviewImage? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            return TryRead(fs);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null;
        }
    }

    public static PreviewImage? TryRead(Stream s)
    {
        try
        {
            var head = Read(s, 0, 16);
            if (head is null) return null;
            if (Encoding.ASCII.GetString(head, 0, 15) == "FUJIFILMCCD-RAW") return FromRaf(s);
            if (Encoding.ASCII.GetString(head, 4, 4) == "ftyp") return FromCr3(s);
            if (head[0] == head[1] && head[0] is (byte)'I' or (byte)'M') return FromTiff(s, 0, s.Length);
            return null;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return null; // malformed file: no preview
        }
    }

    // ------------------------------------------------------------------ TIFF-based RAW

    private static PreviewImage? FromTiff(Stream s, long baseOffset, long end)
    {
        var tiff = new TiffReader(s, baseOffset, end);
        if (!tiff.Valid) return null;

        var candidates = new List<(long Offset, int Length)>();
        var orientation = 1;
        var visited = new HashSet<long>();
        var queue = new Queue<(long Ifd, bool IsIfd0)>();
        queue.Enqueue((tiff.FirstIfd, true));
        while (queue.Count > 0 && visited.Count < MaxIfds)
        {
            var (ifd, isIfd0) = queue.Dequeue();
            if (ifd <= 0 || !visited.Add(ifd)) continue;
            var entries = tiff.ReadIfd(ifd, out var next);
            if (entries is null) continue;
            if (next > 0) queue.Enqueue((next, false));

            long jpegOffset = 0, jpegLength = 0, compression = 0;
            long[]? strips = null, stripCounts = null;
            foreach (var e in entries)
            {
                switch (e.Tag)
                {
                    case 0x0112 when isIfd0: orientation = (int)tiff.Value(e); break;  // Orientation
                    case 0x014A: foreach (var sub in tiff.Values(e)) queue.Enqueue((sub, false)); break; // SubIFDs
                    case 0x0201: jpegOffset = tiff.Value(e); break;                    // JPEGInterchangeFormat
                    case 0x0202: jpegLength = tiff.Value(e); break;                    // JPEGInterchangeFormatLength
                    case 0x0103: compression = tiff.Value(e); break;                   // Compression
                    case 0x0111: strips = tiff.Values(e); break;                       // StripOffsets
                    case 0x0117: stripCounts = tiff.Values(e); break;                  // StripByteCounts
                }
            }
            if (jpegOffset > 0 && jpegLength > 0) candidates.Add((baseOffset + jpegOffset, (int)Math.Min(jpegLength, int.MaxValue)));
            if (compression is 6 or 7 && strips is { Length: 1 } && stripCounts is { Length: 1 })
                candidates.Add((baseOffset + strips[0], (int)Math.Min(stripCounts[0], int.MaxValue)));
        }
        return Pick(s, candidates, orientation);
    }

    // ------------------------------------------------------------------ Canon CR3

    private static PreviewImage? FromCr3(Stream s)
    {
        var candidates = new List<(long Offset, int Length)>();
        var orientation = 1;
        foreach (var box in Boxes(s, 0, s.Length))
        {
            if (box.Type == "moov")
            {
                foreach (var inner in Boxes(s, box.Payload, box.End))
                {
                    if (inner.Type != "uuid" || UuidOf(s, inner) != CanonUuid) continue;
                    foreach (var canon in Boxes(s, inner.Payload + 16, inner.End))
                    {
                        if (canon.Type == "THMB") AddJpegInside(s, canon, candidates);
                        if (canon.Type == "CMT1")
                        {
                            var tiff = new TiffReader(s, canon.Payload, canon.End);
                            var ifd0 = tiff.Valid ? tiff.ReadIfd(tiff.FirstIfd, out _) : null;
                            if (ifd0?.FirstOrDefault(e => e.Tag == 0x0112) is { Tag: 0x0112 } o) orientation = (int)tiff.Value(o);
                        }
                    }
                }
            }
            else if (box.Type == "uuid" && UuidOf(s, box) == CanonPreviewUuid)
            {
                foreach (var prvw in Boxes(s, box.Payload + 16 + 8, box.End)) // uuid id + 8 bytes header
                    if (prvw.Type == "PRVW") AddJpegInside(s, prvw, candidates);
            }
        }
        return Pick(s, candidates, orientation);
    }

    /// <summary>THMB/PRVW boxes hold a small header followed by a JPEG: find the JPEG start.</summary>
    private static void AddJpegInside(Stream s, Box box, List<(long, int)> candidates)
    {
        var head = Read(s, box.Payload, (int)Math.Min(64, box.End - box.Payload));
        if (head is null) return;
        for (var i = 0; i + 2 < head.Length; i++)
        {
            if (head[i] == 0xFF && head[i + 1] == 0xD8 && head[i + 2] == 0xFF)
            {
                candidates.Add((box.Payload + i, (int)(box.End - box.Payload - i)));
                return;
            }
        }
    }

    // ------------------------------------------------------------------ Fujifilm RAF

    private static PreviewImage? FromRaf(Stream s)
    {
        var header = Read(s, 84, 8);
        if (header is null) return null;
        var offset = BinaryPrimitives.ReadUInt32BigEndian(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var candidates = new List<(long, int)> { (offset, (int)Math.Min(length, int.MaxValue)) };
        // Orientation lives in the EXIF of that JPEG.
        var orientation = 1;
        var jpeg = Read(s, offset, (int)Math.Min(length, 128 * 1024));
        if (jpeg is not null && JpegExifTiffStart(jpeg) is { } tiffStart)
        {
            var tiff = new TiffReader(new MemoryStream(jpeg), tiffStart, jpeg.Length);
            var ifd0 = tiff.Valid ? tiff.ReadIfd(tiff.FirstIfd, out _) : null;
            if (ifd0?.FirstOrDefault(e => e.Tag == 0x0112) is { Tag: 0x0112 } o) orientation = (int)tiff.Value(o);
        }
        return Pick(s, candidates, orientation);
    }

    private static int? JpegExifTiffStart(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 4 < jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (marker == 0xE1 && pos + 10 < jpeg.Length && Encoding.ASCII.GetString(jpeg, pos + 4, 4) == "Exif") return pos + 10;
            if (marker is 0xDA or 0xD9) return null;
            pos += 2 + length;
        }
        return null;
    }

    // ------------------------------------------------------------------ choosing a preview

    private static PreviewImage? Pick(Stream s, List<(long Offset, int Length)> candidates, int orientation)
    {
        var valid = new List<(long Offset, int Length, int Side)>();
        foreach (var (offset, length) in candidates.Distinct())
        {
            if (length < 128 || length > MaxPreviewBytes || offset < 0 || offset + length > s.Length) continue;
            var head = Read(s, offset, Math.Min(length, 256 * 1024));
            if (head is null || head[0] != 0xFF || head[1] != 0xD8) continue;
            if (JpegSize(head) is not { } size) continue; // not a viewable JPEG (e.g. lossless RAW data)
            valid.Add((offset, length, Math.Max(size.Width, size.Height)));
        }
        if (valid.Count == 0) return null;
        var best = valid.Where(v => v.Side >= MinPreviewSide).OrderBy(v => v.Side).ThenBy(v => v.Length).FirstOrDefault();
        if (best.Length == 0) best = valid.OrderByDescending(v => v.Side).ThenByDescending(v => v.Length).First();
        var bytes = Read(s, best.Offset, best.Length);
        return bytes is null ? null : new PreviewImage(bytes, orientation is >= 1 and <= 8 ? orientation : 1);
    }

    /// <summary>"WIDTHxHEIGHT" of a JPEG, for diagnostics.</summary>
    public static string JpegSizeOf(byte[] jpeg) => JpegSize(jpeg) is { } s ? $"{s.Width}x{s.Height}" : "?";

    /// <summary>Width and height from the JPEG's SOF marker.</summary>
    internal static (int Width, int Height)? JpegSize(byte[] jpeg)
    {
        var pos = 2;
        while (pos + 9 < jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) return null;
            var marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            // Only baseline/extended/progressive JPEG is a viewable preview. Lossless JPEG (SOF3 and others) is how
            // CR2 and some DNGs store the RAW data itself - it starts with FFD8 too, but it is not a picture.
            if (marker is 0xC0 or 0xC1 or 0xC2)
                return (BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 7)), BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 5)));
            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return null;
            if (marker is 0xDA or 0xD9) return null;
            pos += 2 + length;
        }
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private readonly record struct Box(string Type, long Payload, long End);

    private static IEnumerable<Box> Boxes(Stream s, long start, long end)
    {
        var pos = start;
        var count = 0;
        while (pos + 8 <= end && count++ < 4096)
        {
            var h = Read(s, pos, 8);
            if (h is null) yield break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(h);
            var type = Encoding.Latin1.GetString(h, 4, 4);
            var headerLength = 8L;
            if (size == 1)
            {
                var ext = Read(s, pos + 8, 8);
                if (ext is null) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < headerLength || pos + size > end) yield break;
            yield return new Box(type, pos + headerLength, pos + size);
            pos += size;
        }
    }

    private static Guid? UuidOf(Stream s, Box box) =>
        Read(s, box.Payload, 16) is { } id ? new Guid(id, bigEndian: true) : null;

    private static byte[]? Read(Stream s, long offset, int count)
    {
        if (count < 0 || offset < 0 || offset + count > s.Length) return null;
        s.Position = offset;
        var buffer = new byte[count];
        s.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Minimal TIFF IFD reader (both byte orders), bounded to [start, end).</summary>
    private sealed class TiffReader
    {
        private readonly Stream _s;
        private readonly long _start;
        private readonly long _end;
        private readonly bool _little;

        public readonly record struct Entry(ushort Tag, ushort Type, uint Count, long ValueField);

        public TiffReader(Stream s, long start, long end)
        {
            _s = s;
            _start = start;
            _end = Math.Min(end, s.Length);
            var h = Read(s, start, 8);
            if (h is null) return;
            _little = h[0] == (byte)'I';
            if (h[0] != h[1] || (h[0] != (byte)'I' && h[0] != (byte)'M')) return;
            FirstIfd = U32(h, 4);
            Valid = FirstIfd > 0;
        }

        public bool Valid { get; }
        public long FirstIfd { get; }

        public List<Entry>? ReadIfd(long ifd, out long next)
        {
            next = 0;
            var countBytes = Read(_s, _start + ifd, 2);
            if (countBytes is null || _start + ifd + 2 > _end) return null;
            var count = U16(countBytes, 0);
            if (count is 0 or > 512) return null;
            var raw = Read(_s, _start + ifd + 2, count * 12 + 4);
            if (raw is null) return null;
            var entries = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                var o = i * 12;
                entries.Add(new Entry(U16(raw, o), U16(raw, o + 2), U32(raw, o + 4), _start + ifd + 2 + o + 8));
            }
            next = U32(raw, count * 12);
            return entries;
        }

        public long Value(Entry e) => Values(e).FirstOrDefault();

        public long[] Values(Entry e)
        {
            var size = e.Type switch { 3 => 2, 4 or 13 => 4, _ => 0 };
            if (size == 0 || e.Count is 0 or > 1024) return [];
            var total = (int)(size * e.Count);
            long at = e.ValueField;
            if (total > 4)
            {
                var pointer = Read(_s, e.ValueField, 4);
                if (pointer is null) return [];
                at = _start + U32(pointer, 0);
            }
            var data = Read(_s, at, total);
            if (data is null) return [];
            var values = new long[e.Count];
            for (var i = 0; i < e.Count; i++) values[i] = size == 2 ? U16(data, i * 2) : U32(data, i * 4);
            return values;
        }

        private ushort U16(byte[] b, int o) =>
            _little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));

        private uint U32(byte[] b, int o) =>
            _little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));
    }
}
