using System.Buffers.Binary;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using PhotoShelver.Core.Metadata;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// TIFF metadata is read by jumping to its directories: also when they come after the image (as many scanners and
/// editors write them), only a few kilobytes are read, and the result is exactly what the usual reader returns.
/// </summary>
public class TiffMetadataTests
{
    private static readonly DateTime IfdDate = new(2024, 5, 6, 7, 8, 9);
    private static readonly DateTime OriginalDate = new(2023, 1, 2, 3, 4, 5);

    /// <summary>
    /// A TIFF with <paramref name="pixelBytes"/> of image data first and its directories at the end:
    /// IFD0 with size, DateTime and (optionally) a pointer to an EXIF directory with DateTimeOriginal.
    /// </summary>
    private static byte[] Tiff(bool bigEndian, int pixelBytes, bool withExif)
    {
        var data = new List<byte>();
        void U16(int v) { var b = new byte[2]; if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); data.AddRange(b); }
        void U32(long v) { var b = new byte[4]; if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); data.AddRange(b); }
        static byte[] Ascii(DateTime d) => Encoding.ASCII.GetBytes(d.ToString("yyyy:MM:dd HH:mm:ss") + "\0");

        data.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        var pixelsAt = 8;
        var ifd0At = pixelsAt + pixelBytes;
        var ifd0Count = withExif ? 5 : 4;
        var ifd0Size = 2 + ifd0Count * 12 + 4;
        var dateAt = ifd0At + ifd0Size;
        var exifAt = dateAt + 20;
        var originalAt = exifAt + 2 + 12 + 4;
        U32(ifd0At);
        data.AddRange(new byte[pixelBytes]); // the image comes first

        U16(ifd0Count);
        void Entry(int tag, int type, long count, long value)
        {
            U16(tag); U16(type); U32(count);
            if (type == 3) { U16((int)value); U16(0); } else U32(value);
        }
        Entry(0x0100, 3, 1, 64);                 // ImageWidth
        Entry(0x0101, 3, 1, 48);                 // ImageLength
        Entry(0x0132, 2, 20, dateAt);            // DateTime
        Entry(0x0111, 4, 1, pixelsAt);           // StripOffsets
        if (withExif) Entry(0x8769, 4, 1, exifAt); // ExifIFD
        U32(0);                                  // no next IFD
        data.AddRange(Ascii(IfdDate));
        if (withExif)
        {
            U16(1);
            Entry(0x9003, 2, 20, originalAt);    // DateTimeOriginal
            U32(0);
            data.AddRange(Ascii(OriginalDate));
        }
        return [.. data];
    }

    public static TheoryData<bool, bool> Layouts() => new() { { false, false }, { false, true }, { true, false }, { true, true } };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void DateOfATiffWithTheMetadataAfterTheImage(bool bigEndian, bool withExif)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "scan.tif");
        File.WriteAllBytes(path, Tiff(bigEndian, 2_000_000, withExif));

        var date = new CaptureDateReader().Read(path, MediaKind.Photo, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(withExif ? OriginalDate : IfdDate, date.Local); // DateTimeOriginal wins over DateTime, as for JPEG
    }

    [Fact]
    public void OnlyAFewKilobytesAreRead()
    {
        var tiff = Tiff(bigEndian: false, pixelBytes: 32 * 1024 * 1024, withExif: true);
        using var stream = new CountingStream(new MemoryStream(tiff));

        var directories = CaptureDateReader.ReadTiff(stream);

        Assert.Contains(directories, d => d is ExifSubIfdDirectory);
        Assert.True(stream.BytesRead < 64 * 1024, $"{stream.BytesRead} bytes read of {tiff.Length}");
    }

    [Fact]
    public void TheAppReadsTiffDatesWithoutReadingTheImage()
    {
        // Through the app's own path (CaptureDateReader.Read on a .tif file), not only the helper.
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "scan.tif");
        File.WriteAllBytes(path, Tiff(bigEndian: false, pixelBytes: 32 * 1024 * 1024, withExif: true));
        CountingStream? opened = null;
        var reader = new CaptureDateReader { OpenPhoto = p => opened = new CountingStream(File.OpenRead(p)) };

        var date = reader.Read(path, MediaKind.Photo, DateTime.UtcNow);

        Assert.Equal(OriginalDate, date.Local);
        Assert.NotNull(opened);
        Assert.InRange(opened!.BytesRead, 1, 64 * 1024);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void SameTagsAsTheUsualReader(bool bigEndian, bool withExif)
    {
        AssertSameAsUsual(Tiff(bigEndian, 100_000, withExif));
    }

    [Fact]
    public void SameTagsAsTheUsualReaderForTheScanReplica()
    {
        AssertSameAsUsual(File.ReadAllBytes(Path.Combine(Fixtures.FormatsRoot, "IMG_scan.tif")));
    }

    private static void AssertSameAsUsual(byte[] tiff)
    {
        var usual = ImageMetadataReader.ReadMetadata(new MemoryStream(tiff)).OfType<ExifDirectoryBase>().ToList();
        var seeking = CaptureDateReader.ReadTiff(new MemoryStream(tiff)).OfType<ExifDirectoryBase>().ToList();
        Assert.NotEmpty(usual);
        Assert.Equal(usual.Select(d => d.GetType()), seeking.Select(d => d.GetType()));
        foreach (var (a, b) in usual.Zip(seeking))
            Assert.Equal(a.Tags.Select(t => (t.Type, t.Description)), b.Tags.Select(t => (t.Type, t.Description)));
    }

    [Fact]
    public void AJpegNamedTifIsStillReadByItsContent()
    {
        using var dir = new TempDir();
        var jpeg = Path.Combine(Fixtures.MediaRoot, "photos", "IMG_0001.JPG");
        var renamed = Path.Combine(dir.Path, "IMG_0001.tif");
        File.Copy(jpeg, renamed);
        var reader = new CaptureDateReader();
        var mtime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var expected = reader.Read(jpeg, MediaKind.Photo, mtime);
        Assert.Equal(DateSource.Metadata, expected.Source);
        Assert.Equal(expected, reader.Read(renamed, MediaKind.Photo, mtime));
    }

    [Theory]
    [InlineData(10)]      // header and the start of the image
    [InlineData(100_050)] // cut inside the directory
    public void ADamagedTiffFallsBackToTheFileTime(int keep)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "broken.tif");
        File.WriteAllBytes(path, Tiff(false, 100_000, withExif: true)[..keep]);
        var mtime = new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        var date = new CaptureDateReader(TimeZoneInfo.Utc).Read(path, MediaKind.Photo, mtime);

        Assert.Equal(DateSource.FileModified, date.Source);
        Assert.Equal(new DateTime(2021, 6, 1, 12, 0, 0), date.Local);
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); BytesRead += n; return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
