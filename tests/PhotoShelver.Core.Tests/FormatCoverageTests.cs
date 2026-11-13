using System.Buffers.Binary;
using System.Text;
using PhotoShelver.Core.Metadata;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// The less common photo and video formats of cameras and phones: recognised, dated by their content (not by the
/// extension), never misread, and the camera's proxies are left alone.
/// </summary>
public class FormatCoverageTests
{
    private static readonly DateTime FileTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(".hif", MediaKind.Photo)] [InlineData(".avif", MediaKind.Photo)] [InlineData(".jxl", MediaKind.Photo)]
    [InlineData(".webp", MediaKind.Photo)] [InlineData(".insp", MediaKind.Photo)]
    [InlineData(".crw", MediaKind.Photo)] [InlineData(".nrw", MediaKind.Photo)] [InlineData(".srf", MediaKind.Photo)]
    [InlineData(".sr2", MediaKind.Photo)] [InlineData(".rwl", MediaKind.Photo)] [InlineData(".pef", MediaKind.Photo)]
    [InlineData(".srw", MediaKind.Photo)] [InlineData(".gpr", MediaKind.Photo)] [InlineData(".3fr", MediaKind.Photo)]
    [InlineData(".fff", MediaKind.Photo)] [InlineData(".iiq", MediaKind.Photo)] [InlineData(".erf", MediaKind.Photo)]
    [InlineData(".mef", MediaKind.Photo)] [InlineData(".mos", MediaKind.Photo)] [InlineData(".dcr", MediaKind.Photo)]
    [InlineData(".kdc", MediaKind.Photo)] [InlineData(".mrw", MediaKind.Photo)] [InlineData(".x3f", MediaKind.Photo)]
    [InlineData(".mpg", MediaKind.Video)] [InlineData(".mpeg", MediaKind.Video)] [InlineData(".mod", MediaKind.Video)]
    [InlineData(".tod", MediaKind.Video)] [InlineData(".mkv", MediaKind.Video)] [InlineData(".webm", MediaKind.Video)]
    [InlineData(".insv", MediaKind.Video)] [InlineData(".360", MediaKind.Video)] [InlineData(".crm", MediaKind.Video)]
    [InlineData(".mxf", MediaKind.Video)] [InlineData(".braw", MediaKind.Video)] [InlineData(".r3d", MediaKind.Video)]
    [InlineData(".nev", MediaKind.Video)]
    public void NewFormatsAreRecognised(string extension, MediaKind expected)
    {
        Assert.True(MediaTypes.TryGetKind("FILE0001" + extension.ToUpperInvariant(), out var kind));
        Assert.Equal(expected, kind);
        Assert.True(MediaTypes.TryGetKind("file0001" + extension, out _));
    }

    [Theory]
    [InlineData(".lrv")] // GoPro low-resolution proxy of every video
    [InlineData(".thm")] // GoPro / Canon thumbnail of a video
    [InlineData(".lrf")] // DJI proxy
    [InlineData(".xmp")]
    public void CameraProxiesAndSidecarsAreNotMedia(string extension)
    {
        Assert.False(MediaTypes.TryGetKind("GX010001" + extension, out _));
    }

    [Fact]
    public void EveryRawExtensionIsAPhoto()
    {
        foreach (var ext in MediaTypes.KnownExtensions.Where(e => EmbeddedPreview.IsRaw("x" + e)))
        {
            Assert.True(MediaTypes.TryGetKind("x" + ext, out var kind));
            Assert.Equal(MediaKind.Photo, kind);
        }
        Assert.All(new[] { ".pef", ".srw", ".nrw", ".3fr", ".iiq", ".gpr" }, e => Assert.True(EmbeddedPreview.IsRaw("x" + e)));
    }

    /// <summary>TIFF-based RAW of other makers: the same structure as NEF / ARW, so the same date and preview.</summary>
    [Theory]
    [InlineData("IMG_nikon.NEF", ".nrw")] [InlineData("IMG_nikon.NEF", ".pef")] [InlineData("IMG_nikon.NEF", ".3fr")]
    [InlineData("IMG_nikon.NEF", ".iiq")] [InlineData("IMG_nikon.NEF", ".mos")]
    [InlineData("IMG_sony.ARW", ".srw")] [InlineData("IMG_sony.ARW", ".sr2")] [InlineData("IMG_sony.ARW", ".srf")]
    [InlineData("IMG_sony.ARW", ".rwl")] [InlineData("IMG_sony.ARW", ".erf")] [InlineData("IMG_sony.ARW", ".gpr")]
    [InlineData("IMG_sony.ARW", ".dcr")] [InlineData("IMG_sony.ARW", ".kdc")] [InlineData("IMG_sony.ARW", ".mef")]
    [InlineData("IMG_sony.ARW", ".fff")]
    public void TiffBasedRawOfOtherMakersIsDatedAndPreviewed(string fixture, string extension)
    {
        using var dir = new TempDir();
        var original = Path.Combine(Fixtures.FormatsRoot, fixture);
        var copy = Path.Combine(dir.Path, "IMG_0001" + extension.ToUpperInvariant());
        File.Copy(original, copy);
        var reader = new CaptureDateReader(Fixtures.Berlin);

        var expected = reader.Read(original, MediaKind.Photo, FileTime);
        Assert.Equal(DateSource.Metadata, expected.Source);
        Assert.True(MediaTypes.TryGetKind(copy, out var kind));
        Assert.Equal(expected, reader.Read(copy, kind, FileTime));

        var preview = EmbeddedPreview.TryRead(copy);
        Assert.NotNull(preview);
        Assert.Equal(EmbeddedPreview.TryRead(original)!.Jpeg, preview.Jpeg);
    }

    /// <summary>MP4-type videos under other names (Insta360, GoPro MAX, Canon CRM): dated like an MP4.</summary>
    [Theory]
    [InlineData("VID_pixel.mp4", ".insv")] [InlineData("VID_pixel.mp4", ".360")] [InlineData("VID_canon.MP4", ".crm")]
    [InlineData("VID_canon.MP4", ".mov")] [InlineData("VID_pixel.mp4", ".3gp")] [InlineData("VID_pixel.mp4", ".m4v")]
    public void Mp4TypeVideosUnderOtherNamesAreDated(string fixture, string extension)
    {
        using var dir = new TempDir();
        var original = Path.Combine(Fixtures.FormatsRoot, fixture);
        var copy = Path.Combine(dir.Path, "VID_0001" + extension.ToUpperInvariant());
        File.Copy(original, copy);
        var reader = new CaptureDateReader(Fixtures.Berlin);

        var expected = reader.Read(original, MediaKind.Video, FileTime);
        Assert.Equal(DateSource.Metadata, expected.Source);
        Assert.True(MediaTypes.TryGetKind(copy, out var kind));
        Assert.Equal(MediaKind.Video, kind);
        Assert.Equal(expected, reader.Read(copy, kind, FileTime));
    }

    [Fact]
    public void AnOldQuickTimeFileWithoutFtypIsDated()
    {
        // QuickTime files from older cameras start directly with 'moov', 'mdat', 'wide'…, not 'ftyp'.
        using var dir = new TempDir();
        var original = Path.Combine(Fixtures.MediaRoot, "video", "VID_apple.MOV");
        var bytes = File.ReadAllBytes(original);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(bytes, 4, 4));
        var ftypSize = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var withoutFtyp = Path.Combine(dir.Path, "MVI_0001.MOV");
        File.WriteAllBytes(withoutFtyp, bytes[ftypSize..]);
        var reader = new CaptureDateReader(Fixtures.Berlin);

        var expected = reader.Read(original, MediaKind.Video, FileTime);
        Assert.Equal(DateSource.Metadata, expected.Source);
        Assert.Equal(expected, reader.Read(withoutFtyp, MediaKind.Video, FileTime));
    }

    public static TheoryData<string, byte[]> OtherContainers() => new()
    {
        { "MVI_0001.AVI", Riff("AVI ") },
        { "00000.MTS", [0x00, 0x00, 0x00, 0x00, 0x47, 0x40, 0x00, 0x10, .. new byte[400]] }, // 192-byte TS packets
        { "MOV001.MOD", [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, .. new byte[400]] }, // MPEG program stream
        { "clip.mkv", [0x1A, 0x45, 0xDF, 0xA3, 0x9F, 0x42, 0x86, 0x81, .. new byte[400]] },  // EBML
        { "C0001.MXF", [0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01, .. new byte[400]] }, // MXF partition key
        { "A001.braw", [0x00, 0x00, 0x00, 0x08, 0x62, 0x72, 0x61, 0x77, .. new byte[400]] }, // unknown 'braw' box
        { "not-really.MP4", Riff("AVI ") },  // the content decides, not the name
        { "empty.MP4", [] },
        { "tiny.MOV", [0x00, 0x00, 0x00] },
    };

    [Theory]
    [MemberData(nameof(OtherContainers))]
    public void OtherVideoContainersGetTheFileTimeAndAreNeverMisread(string name, byte[] content)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, content);
        using (var s = File.OpenRead(path)) Assert.False(IsoBmffReader.IsIsoBmff(s));

        var date = new CaptureDateReader(TimeZoneInfo.Utc).Read(path, MediaKind.Video, FileTime);

        Assert.Equal(DateSource.FileModified, date.Source);
        Assert.Equal(new DateTime(2000, 1, 1), date.Local);
    }

    [Fact]
    public void WebPDateIsReadFromItsExif()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "IMG_0001.webp");
        File.WriteAllBytes(path, WebPWithExif(new DateTime(2026, 4, 5, 23, 45, 0)));

        var date = new CaptureDateReader(TimeZoneInfo.Utc).Read(path, MediaKind.Photo, FileTime);

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(2026, 4, 5, 23, 45, 0), date.Local);
    }

    [Theory]
    [InlineData("CRW_0001.CRW", "II\u001a\0\0\0HEAPCCDR")]      // Canon CIFF
    [InlineData("PICT0001.MRW", "\0MRM\0\0\0\u0010PRD\0")]       // Minolta
    [InlineData("SDIM0001.X3F", "FOVb\0\0\u0002\0")]             // Sigma
    [InlineData("IMG_0001.JXL", "ÿ\n")]                     // JPEG XL codestream
    [InlineData("IMG_0001.AVIF", "\0\0\0\u0018ftypavif\0\0\0\0")]
    [InlineData("IMG_0001.HIF", "\0\0\0\u0018ftypheix\0\0\0\0")] // header only, no metadata
    public void PhotosWithoutReadableMetadataGetTheFileTime(string name, string header)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, name);
        File.WriteAllBytes(path, [.. Encoding.Latin1.GetBytes(header), .. new byte[256]]);
        Assert.True(MediaTypes.TryGetKind(path, out var kind));

        var date = new CaptureDateReader(TimeZoneInfo.Utc).Read(path, kind, FileTime);

        Assert.Equal(DateSource.FileModified, date.Source);
        Assert.Null(EmbeddedPreview.TryRead(path));
    }

    private static byte[] Riff(string form)
    {
        var b = new byte[512];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(b.Length - 8));
        Encoding.ASCII.GetBytes(form).CopyTo(b, 8);
        return b;
    }

    /// <summary>A WebP (VP8X) whose EXIF chunk holds a little-endian TIFF with ExifIFD/DateTimeOriginal.</summary>
    private static byte[] WebPWithExif(DateTime taken)
    {
        var tiff = new List<byte>();
        void U16(int v) { var x = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(x, (ushort)v); tiff.AddRange(x); }
        void U32(long v) { var x = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(x, (uint)v); tiff.AddRange(x); }
        tiff.AddRange("II"u8.ToArray()); U16(42); U32(8);
        U16(1); U16(0x8769); U16(4); U32(1); U32(26); U32(0); // IFD0: ExifIFD at 26
        U16(1); U16(0x9003); U16(2); U32(20); U32(44); U32(0); // ExifIFD: DateTimeOriginal at 44
        tiff.AddRange(Encoding.ASCII.GetBytes(taken.ToString("yyyy:MM:dd HH:mm:ss") + "\0"));

        var body = new List<byte>();
        void Chunk(string id, byte[] data)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length); body.AddRange(size);
            body.AddRange(data);
            if (data.Length % 2 == 1) body.Add(0);
        }
        Chunk("VP8X", [0x08, 0, 0, 0, 63, 0, 0, 47, 0, 0]); // EXIF flag, 64 x 48
        Chunk("EXIF", [.. tiff]);

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        var total = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(total, (uint)(4 + body.Count)); file.AddRange(total);
        file.AddRange("WEBP"u8.ToArray());
        file.AddRange(body);
        return [.. file];
    }
}
