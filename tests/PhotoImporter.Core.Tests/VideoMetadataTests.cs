using System.Buffers.Binary;
using System.Text;
using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Tests;

public class VideoMetadataTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    // ---- tiny ISO-BMFF writer ----
    private static byte[] Box(string type, params byte[][] payload)
    {
        var body = payload.SelectMany(b => b).ToArray();
        var result = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(result, 4);
        body.CopyTo(result, 8);
        return result;
    }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Mvhd(DateTime utc)
    {
        var secs = (uint)(utc - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        return Box("mvhd", [0, 0, 0, 0], U32(secs), U32(secs), new byte[88]);
    }

    private static byte[] AppleMeta(params (string Key, string Value)[] items)
    {
        var keys = Box("keys", [0, 0, 0, 0], U32((uint)items.Length),
            items.SelectMany(i => Box("mdta", Utf8(i.Key)).Skip(0)).ToArray());
        // 'mdta' boxes above use the namespace as type — exactly the keys entry layout.
        var ilstItems = items.Select((it, idx) =>
        {
            var index = Encoding.Latin1.GetString(U32((uint)idx + 1));
            return Box(index, Box("data", U32(1), U32(0), Utf8(it.Value)));
        }).ToArray();
        return Box("meta", Box("hdlr", new byte[25]), keys, Box("ilst", ilstItems));
    }

    private static MemoryStream Mp4(params byte[][] moovChildren) =>
        new([.. Box("ftyp", Utf8("qt  "), U32(0)), .. Box("moov", moovChildren), .. Box("mdat", new byte[16])]);

    [Fact]
    public void ReadsMvhdCreationTime()
    {
        var utc = new DateTime(2025, 8, 1, 22, 15, 0, DateTimeKind.Utc);
        var info = IsoBmffReader.Read(Mp4(Mvhd(utc)));
        Assert.Equal(utc, info.CreationUtc);
        Assert.Null(info.AppleCreationDate);
    }

    [Fact]
    public void ReadsAppleKeys()
    {
        var info = IsoBmffReader.Read(Mp4(
            Mvhd(new DateTime(2025, 8, 1, 22, 15, 0, DateTimeKind.Utc)),
            AppleMeta(
                ("com.apple.quicktime.location.ISO6709", "+35.6812+139.7671+040.000/"),
                ("com.apple.quicktime.creationdate", "2025-08-02T07:15:00+0900"))));
        Assert.Equal("2025-08-02T07:15:00+0900", info.AppleCreationDate);
        Assert.Equal("+35.6812+139.7671+040.000/", info.Iso6709Location);
    }

    [Fact]
    public void ReadsUdtaXyz()
    {
        var loc = "+48.8577+002.2950/";
        var xyz = Box("©xyz", U16((ushort)loc.Length), U16(0x15c7), Utf8(loc));
        var info = IsoBmffReader.Read(Mp4(Mvhd(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), Box("udta", xyz)));
        Assert.Equal(loc, info.Iso6709Location);
    }

    [Fact]
    public void GarbageIsHarmless()
    {
        var info = IsoBmffReader.Read(new MemoryStream(Encoding.ASCII.GetBytes("this is not an mp4 file at all")));
        Assert.Null(info.CreationUtc);
    }

    [Fact]
    public void AppleDateWithOffsetIsUsedAsLocal()
    {
        var reader = new CaptureDateReader(Berlin);
        var date = reader.ResolveVideoDate(new IsoBmffInfo(
            new DateTime(2025, 8, 1, 22, 15, 0, DateTimeKind.Utc), "2025-08-02T07:15:00+0900", null));
        Assert.Equal(new CaptureDate(new DateTime(2025, 8, 2, 7, 15, 0), DateSource.Metadata), date);
    }

    [Fact]
    public void GpsDeterminesZone()
    {
        // 22:15 UTC in Tokyo is 07:15 next day; in Berlin it would still be Aug 2 00:15.
        var reader = new CaptureDateReader(Berlin);
        var date = reader.ResolveVideoDate(new IsoBmffInfo(
            new DateTime(2025, 8, 1, 22, 15, 0, DateTimeKind.Utc), null, "+35.6812+139.7671/"));
        Assert.Equal(new CaptureDate(new DateTime(2025, 8, 2, 7, 15, 0), DateSource.Metadata), date);
    }

    [Fact]
    public void WithoutGpsUsesComputerZoneWithDst()
    {
        var reader = new CaptureDateReader(Berlin);
        var summer = reader.ResolveVideoDate(new IsoBmffInfo(new DateTime(2025, 8, 1, 22, 15, 0, DateTimeKind.Utc), null, null));
        var winter = reader.ResolveVideoDate(new IsoBmffInfo(new DateTime(2025, 1, 1, 22, 15, 0, DateTimeKind.Utc), null, null));
        Assert.Equal(new CaptureDate(new DateTime(2025, 8, 2, 0, 15, 0), DateSource.MetadataGuessedZone), summer);
        Assert.Equal(new CaptureDate(new DateTime(2025, 1, 1, 23, 15, 0), DateSource.MetadataGuessedZone), winter);
    }

    [Fact]
    public void NoMetadataReturnsNull()
    {
        Assert.Null(new CaptureDateReader(Berlin).ResolveVideoDate(new IsoBmffInfo(null, null, null)));
    }

    [Theory]
    [InlineData("+48.8577+002.2950+035.000/", 48.8577, 2.2950)]
    [InlineData("-33.8688+151.2093/", -33.8688, 151.2093)]
    [InlineData("+4851.462+00217.700/", 48.8577, 2.295)]
    public void ParsesIso6709(string s, double lat, double lon)
    {
        Assert.True(Iso6709.TryParse(s, out var la, out var lo));
        Assert.Equal(lat, la, 3);
        Assert.Equal(lon, lo, 3);
    }

    [Theory]
    [InlineData("2025-08-02T07:15:00+0900", false)]
    [InlineData("2025-08-02T07:15:00+09:00", false)]
    [InlineData("2025-08-02T07:15:00Z", true)]
    public void ParsesAppleDate(string s, bool utc)
    {
        Assert.True(CaptureDateReader.TryParseAppleDate(s, out var local, out var isUtc));
        Assert.Equal(new DateTime(2025, 8, 2, 7, 15, 0), local);
        Assert.Equal(utc, isUtc);
    }

    [Fact]
    public void FileWithoutMetadataFallsBackToModifiedTime()
    {
        using var dir = new TempDir();
        var modified = new DateTime(2024, 12, 31, 23, 30, 0, DateTimeKind.Utc);
        var photo = dir.File("broken.jpg", "not a jpeg", modified);
        var video = dir.File("broken.mp4", "not an mp4", modified);
        var reader = new CaptureDateReader(Berlin);

        var expected = new CaptureDate(new DateTime(2025, 1, 1, 0, 30, 0), DateSource.FileModified);
        Assert.Equal(expected, reader.Read(photo, MediaKind.Photo, modified));
        Assert.Equal(expected, reader.Read(video, MediaKind.Video, modified));
    }
}
