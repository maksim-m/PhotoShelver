using System.Buffers.Binary;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoShelver.Core.Metadata;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// Dates of very large TIFFs: metadata beyond 2 GB in a classic TIFF (up to 4 GB is allowed) and BigTIFF (64-bit
/// offsets). The metadata library fails on both; our own reader takes over. The files are virtual streams of the
/// full length (zeros except the TIFF structures), so the tests need no disk space.
/// </summary>
public class LargeTiffTests
{
    private static readonly DateTime IfdDate = new(2024, 5, 6, 7, 8, 9);
    private static readonly DateTime OriginalDate = new(2023, 1, 2, 3, 4, 5);
    private static readonly DateTime DigitizedDate = new(2022, 3, 4, 5, 6, 7);
    private const long GB = 1024L * 1024 * 1024;

    [Flags]
    public enum Dates { None = 0, DateTime = 1, Original = 2, Digitized = 4, All = 7 }

    /// <summary>
    /// A TIFF of <paramref name="length"/> bytes with its IFD0 (and the EXIF IFD right after it) at
    /// <paramref name="metadataAt"/>; zeros everywhere else, as for the image data.
    /// </summary>
    private static SparseStream Tiff(bool bigTiff, bool bigEndian, long metadataAt, long length, Dates dates = Dates.All)
    {
        var chunks = new SortedList<long, byte[]>();
        var w = new Writer(bigEndian);
        w.Bytes(bigEndian ? "MM"u8 : "II"u8);
        if (bigTiff) { w.U16(43); w.U16(8); w.U16(0); w.U64(metadataAt); }
        else { w.U16(42); w.U32(metadataAt); }
        chunks.Add(0, w.ToArray());

        var withExif = (dates & (Dates.Original | Dates.Digitized)) != 0;
        var entrySize = bigTiff ? 20 : 12;
        var countSize = bigTiff ? 8 : 2;
        var fieldSize = bigTiff ? 8 : 4;
        var ifd0Entries = 2 + ((dates & Dates.DateTime) != 0 ? 1 : 0) + (withExif ? 1 : 0);
        var ifd0Size = countSize + ifd0Entries * entrySize + fieldSize;
        var exifEntries = ((dates & Dates.Original) != 0 ? 1 : 0) + ((dates & Dates.Digitized) != 0 ? 1 : 0);
        var exifAt = metadataAt + ifd0Size;
        var exifSize = withExif ? countSize + exifEntries * entrySize + fieldSize : 0;
        var stringsAt = exifAt + exifSize;

        w = new Writer(bigEndian);
        void Count(int n) { if (bigTiff) w.U64(n); else w.U16(n); }
        void Entry(int tag, int type, long count, long value)
        {
            w.U16(tag); w.U16(type);
            if (bigTiff) { w.U64(count); if (type == 3) { w.U16((int)value); w.U16(0); w.U32(0); } else w.U64(value); }
            else { w.U32(count); if (type == 3) { w.U16((int)value); w.U16(0); } else w.U32(value); }
        }
        void Next() { if (bigTiff) w.U64(0); else w.U32(0); }

        var strings = new List<DateTime>();
        long StringAt(DateTime d) { strings.Add(d); return stringsAt + (strings.Count - 1) * 20; }

        Count(ifd0Entries);
        Entry(0x0100, 3, 1, 64); // ImageWidth
        Entry(0x0101, 3, 1, 48); // ImageLength
        if ((dates & Dates.DateTime) != 0) Entry(0x0132, 2, 20, StringAt(IfdDate));
        if (withExif) Entry(0x8769, bigTiff ? 18 : 4, 1, exifAt); // ExifIFD (IFD8 in BigTIFF)
        Next();
        if (withExif)
        {
            Count(exifEntries);
            if ((dates & Dates.Original) != 0) Entry(0x9003, 2, 20, StringAt(OriginalDate));
            if ((dates & Dates.Digitized) != 0) Entry(0x9004, 2, 20, StringAt(DigitizedDate));
            Next();
        }
        foreach (var d in strings) w.Bytes(Encoding.ASCII.GetBytes(d.ToString("yyyy:MM:dd HH:mm:ss") + "\0"));
        chunks.Add(metadataAt, w.ToArray());
        return new SparseStream(Math.Max(length, metadataAt + chunks[metadataAt].Length), chunks);
    }

    /// <summary>Reads through the app's own path; the file itself does not exist, so only the stream can give a date.</summary>
    private static CaptureDate ReadThroughTheApp(Stream tiff)
    {
        var reader = new CaptureDateReader(TimeZoneInfo.Utc) { OpenPhoto = _ => tiff };
        return reader.Read(Path.Combine(TempDir.Root, "does-not-exist", "scan.tif"), MediaKind.Photo,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public static TheoryData<bool, bool, long> Placements() => new()
    {
        // classic TIFF: within 2 GB (the library reads it), beyond 2 GB, up to the 4 GB limit of its 32-bit offsets
        { false, false, 4096 }, { false, true, 4096 },
        { false, false, 2 * GB + 512 }, { false, true, 2 * GB + 512 },
        { false, false, 4 * GB - 4096 }, { false, true, 4 * GB - 4096 },
        // BigTIFF: small, beyond 4 GB, far beyond
        { true, false, 4096 }, { true, true, 4096 },
        { true, false, 6 * GB }, { true, true, 6 * GB },
        { true, false, 300 * GB }, { true, true, 300 * GB },
    };

    [Theory]
    [MemberData(nameof(Placements))]
    public void TheDateIsReadWhereverTheMetadataIs(bool bigTiff, bool bigEndian, long metadataAt)
    {
        var date = ReadThroughTheApp(Tiff(bigTiff, bigEndian, metadataAt, metadataAt + 1024));

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(OriginalDate, date.Local);
    }

    [Theory]
    [InlineData(false, 2 * 1024L * 1024 * 1024 + 512)]
    [InlineData(false, 4 * 1024L * 1024 * 1024 - 4096)]
    public void TheLibraryAloneCannotReadThese(bool bigTiff, long metadataAt)
    {
        // Why our own reader exists: without it these files would get the file time. (BigTIFF the library can read,
        // but it is never given one: see CaptureDateReader.MetadataStackSize.)
        List<MetadataExtractor.Directory>? directories = null;
        try { directories = CaptureDateReader.ReadTiff(Tiff(bigTiff, false, metadataAt, metadataAt + 1024)); }
        catch (Exception) { }
        Assert.True(directories is null || !directories.OfType<ExifSubIfdDirectory>().Any(d => d.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal)),
            "the library read it after all: the test no longer shows the reason for our reader");
    }

    [Theory]
    [InlineData(Dates.All)]
    [InlineData(Dates.DateTime)]
    [InlineData(Dates.Digitized)]
    [InlineData(Dates.DateTime | Dates.Digitized)]
    [InlineData(Dates.None)]
    public void TheSameDateIsChosenAsForSmallFiles(Dates dates)
    {
        // Same priority as everywhere: DateTimeOriginal, then DateTimeDigitized, then DateTime; none: the file time.
        var small = ReadThroughTheApp(Tiff(false, false, 4096, 8192, dates));
        foreach (var (bigTiff, at) in new[] { (false, 3 * GB), (true, 4096L), (true, 9 * GB) })
            Assert.Equal(small, ReadThroughTheApp(Tiff(bigTiff, false, at, at + 1024, dates)));

        var expected = (dates & Dates.Original) != 0 ? OriginalDate
            : (dates & Dates.Digitized) != 0 ? DigitizedDate
            : (dates & Dates.DateTime) != 0 ? IfdDate : (DateTime?)null;
        Assert.Equal(expected ?? new DateTime(2000, 1, 1), small.Local);
        Assert.Equal(expected is null ? DateSource.FileModified : DateSource.Metadata, small.Source);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void OurReaderGivesTheLibrarysValuesWhereBothCanRead(bool bigEndian, bool useFixture)
    {
        var bytes = useFixture
            ? File.ReadAllBytes(Path.Combine(Fixtures.FormatsRoot, "IMG_scan.tif"))
            : ToArray(Tiff(false, bigEndian, 100_000, 101_000));
        var library = CaptureDateReader.ReadTiff(new MemoryStream(bytes));
        var ours = CaptureDateReader.ReadTiffDates(new MemoryStream(bytes));

        int[] tags = [ExifDirectoryBase.TagDateTime, ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagDateTimeDigitized];
        static string?[] Values(IEnumerable<MetadataExtractor.Directory> dirs, int[] tags) =>
            [.. tags.SelectMany(t => new[]
            {
                dirs.OfType<ExifIfd0Directory>().Select(d => d.TryGetDateTime(t, out var v) ? v.ToString("O") : null).FirstOrDefault(),
                dirs.OfType<ExifSubIfdDirectory>().Select(d => d.TryGetDateTime(t, out var v) ? v.ToString("O") : null).FirstOrDefault(),
            })];
        if (!useFixture) Assert.Contains(Values(library, tags), v => v is not null); // the scan replica has no dates: both say so
        Assert.Equal(Values(library, tags), Values(ours, tags));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyAFewKilobytesAreRead(bool bigTiff)
    {
        var stream = Tiff(bigTiff, false, 3 * GB, 3 * GB + 1024);

        var date = ReadThroughTheApp(stream);

        Assert.Equal(OriginalDate, date.Local);
        Assert.InRange(stream.BytesRead, 1, 64 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedFilesNeverThrowAndGetAValidAnswer(bool bigTiff)
    {
        // Every byte of the structures overwritten with random values, many times: the reader never fails with an
        // exception, never reads outside the file, and the app always gets a date (from the metadata or the file).
        var original = ToArray(Tiff(bigTiff, false, 512, 2048));
        var random = new Random(20261113);
        for (var i = 0; i < 3000; i++)
        {
            var bytes = (byte[])original.Clone();
            for (var k = random.Next(1, 6); k > 0; k--)
            {
                bytes[random.Next(0, 800)] = (byte)random.Next(256); // the header and all structures
            }
            if (random.Next(8) == 0) Array.Resize(ref bytes, random.Next(0, bytes.Length));

            var exception = Record.Exception(() => CaptureDateReader.ReadTiffDates(new MemoryStream(bytes)));
            Assert.Null(exception);
            var date = ReadThroughTheApp(new MemoryStream(bytes));
            Assert.True(date.Source is DateSource.Metadata or DateSource.FileModified);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointersOutsideTheFileOrInACircleAreIgnored(bool bigTiff)
    {
        var bytes = ToArray(Tiff(bigTiff, false, 512, 2048));
        var fieldSize = bigTiff ? 8 : 4;
        var entrySize = bigTiff ? 20 : 12;
        var ifd0 = 512 + (bigTiff ? 8 : 2);
        // IFD0 entries: width, height, DateTime, ExifIFD. Value field offset inside an entry: 12 (BigTIFF) or 8.
        var dateField = ifd0 + 2 * entrySize + (bigTiff ? 12 : 8);
        var exifField = ifd0 + 3 * entrySize + (bigTiff ? 12 : 8);

        foreach (var bad in new[] { 0L, 4000L, uint.MaxValue, bigTiff ? long.MaxValue : uint.MaxValue - 1, 512L /* IFD0 itself */ })
        {
            var copy = (byte[])bytes.Clone();
            Write(copy, exifField, bad, fieldSize);
            Write(copy, dateField, bad == 512 ? 1 : bad, fieldSize);
            var dirs = CaptureDateReader.ReadTiffDates(new MemoryStream(copy));
            Assert.DoesNotContain(dirs, d => d.ContainsTag(ExifDirectoryBase.TagDateTimeOriginal));
            Assert.Equal(DateSource.FileModified, ReadThroughTheApp(new MemoryStream(copy)).Source);
        }
    }

    /// <summary>
    /// A classic TIFF whose three nested directories (IFD0 -> EXIF -> Interop) have the maximum 65,535 entries each, as
    /// a damaged count can make them. The metadata library puts each table on the stack (786 KB each): on a normal
    /// thread this is a stack overflow, which ends the whole process. The same structure inside a RAW file reaches the
    /// same code. The app reads it on its large-stack thread: no crash, and the date is still found.
    /// </summary>
    [Theory]
    [InlineData("scan.tif")]
    [InlineData("IMG_0001.CR2")]
    [InlineData("IMG_0001.DNG")]
    public void HugeNestedDirectoriesDoNotEndTheApp(string name)
    {
        const int entries = 0xFFFF;
        var w = new Writer(bigEndian: false);
        long ifdSize = 2 + entries * 12 + 4;
        long ifd0 = 8, exif = ifd0 + ifdSize, interop = exif + ifdSize, dateAt = interop + ifdSize;
        w.Bytes("II"u8); w.U16(42); w.U32(ifd0);
        void Ifd(int pointerTag, long pointerTo, Action? extra)
        {
            w.U16(entries);
            var written = 0;
            if (pointerTag != 0) { Entry(pointerTag, 4, 1, pointerTo); written++; }
            if (extra is not null) { extra(); written++; }
            for (; written < entries; written++) Entry(0x0100 + written % 2, 3, 1, 64); // harmless sizes
            w.U32(0);
        }
        void Entry(int tag, int type, long count, long value) { w.U16(tag); w.U16(type); w.U32(count); w.U32(value); }
        Ifd(0x8769, exif, () => Entry(0x0132, 2, 20, dateAt));  // IFD0: ExifIFD and DateTime
        Ifd(0xA005, interop, null);                             // EXIF: Interop
        Ifd(0, 0, null);                                        // Interop
        w.Bytes(Encoding.ASCII.GetBytes(IfdDate.ToString("yyyy:MM:dd HH:mm:ss") + "\0"));
        var bytes = w.ToArray();

        var reader = new CaptureDateReader(TimeZoneInfo.Utc) { OpenPhoto = _ => new MemoryStream(bytes) };
        var date = reader.Read(Path.Combine(TempDir.Root, "does-not-exist", name), MediaKind.Photo,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(IfdDate, date.Local);
    }

    /// <summary>
    /// A BigTIFF whose directory claims 15 million entries (a damaged count): the library would put a 300 MB table on
    /// the stack, more than even the large stack (the 1 GB file has room for the table, so the library does not reject it),
    /// so a BigTIFF is never given to it - whatever the file is called.
    /// </summary>
    [Theory]
    [InlineData("scan.tif")]
    [InlineData("IMG_0001.CR2")]
    [InlineData("IMG_0001.JPG")]
    public void ABigTiffWithAHugeDirectoryDoesNotEndTheApp(string name)
    {
        var count = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(count, 15_000_000);
        var chunks = new SortedList<long, byte[]> { [0] = ToArray(Tiff(true, false, 4096, 4096)).AsSpan(0, 16).ToArray(), [4096] = count };
        var damaged = new SparseStream(1 * GB, chunks); // under 2 GB: larger streams make the library stop earlier

        var reader = new CaptureDateReader(TimeZoneInfo.Utc) { OpenPhoto = _ => damaged };
        var date = reader.Read(Path.Combine(TempDir.Root, "does-not-exist", name), MediaKind.Photo,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateSource.FileModified, date.Source);
        Assert.InRange(damaged.BytesRead, 1, 64 * 1024);
    }

    [Fact]
    public void ABigTiffHeaderWithAnUnknownOffsetSizeIsNotRead()
    {
        var bytes = ToArray(Tiff(true, false, 512, 2048));
        bytes[4] = 4; // offset size must be 8
        Assert.Empty(CaptureDateReader.ReadTiffDates(new MemoryStream(bytes)));
    }

    private static void Write(byte[] b, int at, long value, int size)
    {
        if (size == 8) BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(at), value);
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), (uint)value);
    }

    private static byte[] ToArray(Stream s)
    {
        var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    private sealed class Writer(bool bigEndian)
    {
        private readonly List<byte> _data = [];
        public void Bytes(ReadOnlySpan<byte> b) => _data.AddRange(b.ToArray());
        public void U16(int v) { var b = new byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); _data.AddRange(b); }
        public void U32(long v) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); _data.AddRange(b); }
        public void U64(long v) { var b = new byte[8]; if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)v); else BinaryPrimitives.WriteUInt64LittleEndian(b, (ulong)v); _data.AddRange(b); }
        public byte[] ToArray() => [.. _data];
    }

    /// <summary>A read-only stream of the given length: the chunks at their positions, zeros elsewhere. Counts bytes read.</summary>
    private sealed class SparseStream(long length, SortedList<long, byte[]> chunks) : Stream
    {
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Clamp(length - Position, 0, count);
            Array.Clear(buffer, offset, n);
            foreach (var (at, data) in chunks)
            {
                var from = Math.Max(at, Position);
                var to = Math.Min(at + data.Length, Position + n);
                if (from < to) Array.Copy(data, from - at, buffer, offset + (from - Position), to - from);
            }
            Position += n;
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            _ => length + offset,
        };

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
