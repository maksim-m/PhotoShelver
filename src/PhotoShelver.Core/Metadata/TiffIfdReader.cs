using System.Buffers.Binary;

namespace PhotoShelver.Core.Metadata;

/// <summary>
/// Minimal TIFF directory (IFD) reader, both byte orders, classic TIFF and BigTIFF, bounded to [start, end).
/// Offsets are kept as 64-bit numbers: classic TIFF allows them up to 4 GB, BigTIFF beyond.
/// Never reads more than an IFD or a value at a time; anything out of bounds reads as missing.
/// </summary>
internal sealed class TiffIfdReader
{
    private const int MaxEntries = 512;
    private const int MaxValues = 1024;

    private readonly Stream _s;
    private readonly long _start;
    private readonly long _end;
    private readonly bool _little;

    /// <param name="ValueField">Absolute position of the entry's value field (the value itself or a pointer to it).</param>
    public readonly record struct Entry(ushort Tag, ushort Type, long Count, long ValueField);

    public TiffIfdReader(Stream s, long start, long end)
    {
        _s = s;
        _start = start;
        _end = Math.Min(end, s.Length);
        var h = Read(start, 8);
        if (h is null || h[0] != h[1] || (h[0] != (byte)'I' && h[0] != (byte)'M')) return;
        _little = h[0] == (byte)'I';
        if (U16(h, 2) == 43)
        {
            // BigTIFF: offset size 8, a reserved field (zero, but not checked - the metadata library does not check it
            // either), then the 8-byte offset of the first IFD. Any header with 43 counts as BigTIFF, readable or not,
            // so that it is never given to the library (see CaptureDateReader.MetadataStackSize).
            IsBigTiff = true;
            var big = Read(start, 16);
            if (big is null || U16(big, 4) != 8) return;
            FirstIfd = Offset(U64(big, 8));
        }
        else
        {
            // Classic TIFF (42), and the variants of RAW formats with their own magic number (ORF, RW2).
            FirstIfd = U32(h, 4);
        }
        Valid = FirstIfd > 0;
    }

    public bool Valid { get; }
    public bool IsBigTiff { get; }
    public long FirstIfd { get; }

    private int EntrySize => IsBigTiff ? 20 : 12;
    private int FieldSize => IsBigTiff ? 8 : 4;

    public List<Entry>? ReadIfd(long ifd, out long next)
    {
        next = 0;
        if (ifd <= 0) return null;
        var countSize = IsBigTiff ? 8 : 2;
        var countBytes = Read(_start + ifd, countSize);
        if (countBytes is null) return null;
        var count = IsBigTiff ? U64(countBytes, 0) : U16(countBytes, 0);
        if (count is 0 or > MaxEntries) return null;
        var n = (int)count;
        var at = _start + ifd + countSize;
        var raw = Read(at, n * EntrySize + FieldSize);
        if (raw is null) return null;
        var entries = new List<Entry>(n);
        for (var i = 0; i < n; i++)
        {
            var o = i * EntrySize;
            var valueCount = IsBigTiff ? U64(raw, o + 4) : U32(raw, o + 4);
            entries.Add(new Entry(U16(raw, o), U16(raw, o + 2), (long)Math.Min(valueCount, long.MaxValue), at + o + (IsBigTiff ? 12 : 8)));
        }
        next = IsBigTiff ? Offset(U64(raw, n * EntrySize)) : U32(raw, n * EntrySize);
        return entries;
    }

    public long Value(Entry e) => Values(e).FirstOrDefault();

    /// <summary>Integer values: SHORT, LONG, IFD and (BigTIFF) LONG8, IFD8. Other types read as none.</summary>
    public long[] Values(Entry e)
    {
        var size = e.Type switch { 3 => 2, 4 or 13 => 4, 16 or 18 => 8, _ => 0 };
        if (size == 0 || e.Count is <= 0 or > MaxValues) return [];
        var data = Data(e, (int)(size * e.Count));
        if (data is null) return [];
        var values = new long[e.Count];
        for (var i = 0; i < e.Count; i++)
            values[i] = size switch { 2 => U16(data, i * 2), 4 => U32(data, i * 4), _ => Offset(U64(data, i * 8)) };
        return values;
    }

    /// <summary>The bytes of an ASCII value (without interpretation), at most <paramref name="maxLength"/>; null when not ASCII.</summary>
    public byte[]? Ascii(Entry e, int maxLength)
    {
        if (e.Type != 2 || e.Count is <= 0 || e.Count > maxLength) return null;
        return Data(e, (int)e.Count);
    }

    /// <summary>A value's bytes: in the value field itself when they fit, otherwise where its pointer says.</summary>
    private byte[]? Data(Entry e, int total)
    {
        var at = e.ValueField;
        if (total > FieldSize)
        {
            var pointer = Read(e.ValueField, FieldSize);
            if (pointer is null) return null;
            var offset = IsBigTiff ? Offset(U64(pointer, 0)) : U32(pointer, 0);
            if (offset <= 0) return null;
            at = _start + offset;
        }
        return Read(at, total);
    }

    /// <summary>A BigTIFF offset as a position; beyond any possible file it reads as none (0).</summary>
    private static long Offset(ulong value) => value > long.MaxValue / 2 ? 0 : (long)value;

    private byte[]? Read(long offset, int count)
    {
        if (count < 0 || offset < _start || offset > _end - count) return null;
        _s.Position = offset;
        var buffer = new byte[count];
        _s.ReadExactly(buffer);
        return buffer;
    }

    private ushort U16(byte[] b, int o) =>
        _little ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));

    private uint U32(byte[] b, int o) =>
        _little ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    private ulong U64(byte[] b, int o) =>
        _little ? BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o));
}
