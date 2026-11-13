using System.Buffers.Binary;
using System.Text;
using PhotoShelver.Core.Metadata;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// Video dates from the makers' own fields, in the layouts of real camera files (checked on public samples of each
/// kind; the files here are synthetic, with invented values). Cameras differ in what they write into the standard
/// mvhd time - UTC as the specification says (phones, Sony) or their own clock (Canon, GoPro) - so the maker's own
/// field is preferred, and times are chosen so that a wrong interpretation lands in the neighbouring day.
/// </summary>
public class CameraVideoDateTests
{
    private static readonly DateTime FileTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- tiny writers

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

    /// <summary>mvhd with the given wall time stored as is (whatever the camera meant by it).</summary>
    private static byte[] Mvhd(DateTime stored)
    {
        var secs = (uint)(stored - new DateTime(1904, 1, 1)).TotalSeconds;
        return Box("mvhd", [0, 0, 0, 0], U32(secs), U32(secs), new byte[88]);
    }

    private static CaptureDate Read(byte[] file, string name, TimeZoneInfo? zone = null)
    {
        var reader = new CaptureDateReader(zone ?? Fixtures.Berlin) { OpenMedia = _ => new MemoryStream(file) };
        return reader.Read(Path.Combine(TempDir.Root, "does-not-exist", name), MediaKind.Video, FileTime);
    }

    // ---------------------------------------------------------------- Canon MOV

    [Fact]
    public void CanonMovUsesTheCameraExifInUdta()
    {
        // EOS DSLRs (5D, 6D, 7D…) write MOV files with moov/udta/CNTH/CNDA (MP4 files: inside a uuid box); mvhd holds
        // the same camera clock. Without the EXIF it would be read as UTC: 23:40 -> 01:40 next day in Berlin summer.
        var jpeg = IsoBmffReader.Read(Path.Combine(Fixtures.FormatsRoot, "VID_canon.MP4")).EmbeddedJpeg!;
        var mov = (byte[])[.. Box("ftyp", "qt  "u8.ToArray(), U32(0)),
            .. Box("moov", Mvhd(new DateTime(2026, 7, 14, 23, 40, 0)), Box("udta", Box("CNCV", "CanonAVC0010"u8.ToArray()), Box("CNTH", Box("CNDA", jpeg)))),
            .. Box("mdat", new byte[16])];

        var date = Read(mov, "MVI_0001.MOV");

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(2026, 7, 14, 23, 40, 0), date.Local);
    }

    // ---------------------------------------------------------------- Sony XML

    private static byte[] SonyMp4(DateTime mvhdUtc, string? creationDate) =>
    [
        .. Box("ftyp", "XAVC"u8.ToArray(), U32(0x100), "XAVCmp42iso2"u8.ToArray()),
        .. Box("mdat", new byte[16]),
        .. Box("moov", Mvhd(mvhdUtc)),
        // A top-level full-box meta next to moov: hdlr, then a full-box 'xml ' with NonRealTimeMeta.
        .. (creationDate is null ? [] : Box("meta", [0, 0, 0, 0], Box("hdlr", new byte[4], "\0\0\0\0meta"u8.ToArray(), new byte[12], [0]),
            Box("xml ", [0, 0, 0, 0], Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<NonRealTimeMeta xmlns=\"urn:schemas-professionalDisc:nonRealTimeMeta:ver.2.00\" lastUpdate=\"2026-07-14T21:40:00Z\">\n" +
                "\t<Duration value=\"252\"/>\n\t<LtcChangeTable tcFps=\"25\"><LtcChange frameCount=\"0\" value=\"19060600\" status=\"increment\"/></LtcChangeTable>\n" +
                $"\t<CreationDate value=\"{creationDate}\"/>\n\t<Device manufacturer=\"Sony\" modelName=\"ILCE-0000\"/>\n</NonRealTimeMeta>\n")))),
    ];

    [Fact]
    public void SonyXmlWithAZoneOffsetGivesTheExactLocalTime()
    {
        // Shot at 23:40 in Tokyo (+09:00): Berlin's zone must not matter.
        var date = Read(SonyMp4(new DateTime(2026, 7, 14, 14, 40, 0), "2026-07-14T23:40:00+09:00"), "C0001.MP4");

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(2026, 7, 14, 23, 40, 0), date.Local);
    }

    [Fact]
    public void SonyXmlInUtcIsTreatedLikeMvhd()
    {
        // Older Sony cameras write 'Z' (UTC), the same as mvhd: the zone is still a guess, and it is marked as such.
        var date = Read(SonyMp4(new DateTime(2026, 7, 14, 22, 40, 0), "2026-07-14T22:40:00Z"), "C0001.MP4");

        Assert.Equal(DateSource.MetadataGuessedZone, date.Source);
        Assert.Equal(new DateTime(2026, 7, 15, 0, 40, 0), date.Local);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("2026-13-45T25:61:00+02:00")]
    public void ABrokenSonyDateFallsBackToMvhd(string value)
    {
        var date = Read(SonyMp4(new DateTime(2026, 7, 14, 22, 40, 0), value), "C0001.MP4");

        Assert.Equal(DateSource.MetadataGuessedZone, date.Source);
        Assert.Equal(new DateTime(2026, 7, 15, 0, 40, 0), date.Local);
    }

    // ---------------------------------------------------------------- GoPro

    private static byte[] GoProMp4(DateTime mvhd, params byte[][] udta) =>
        [.. Box("ftyp", "mp41"u8.ToArray(), U32(0)), .. Box("mdat", new byte[16]), .. Box("moov", Mvhd(mvhd), Box("udta", udta))];

    [Theory]
    [InlineData("HD6.01.02.01.00")]  // HERO6
    [InlineData("HD8.01.02.51.00")]  // HERO8
    [InlineData("H22.01.01.10.70")]  // HERO11
    [InlineData("H19.03.02.00.00")]  // MAX
    public void GoProWritesItsOwnClockIntoMvhd(string firmware)
    {
        // Verified on a HERO6 file: mvhd was 2 hours ahead of the GPS (UTC) time in its telemetry, in June in Europe.
        var date = Read(GoProMp4(new DateTime(2026, 7, 14, 23, 40, 0), Box("FIRM", Encoding.ASCII.GetBytes(firmware))), "GX010001.MP4");

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(2026, 7, 14, 23, 40, 0), date.Local);
    }

    [Fact]
    public void GoProTelemetrySettingsAloneAlsoMarkAGoPro()
    {
        var date = Read(GoProMp4(new DateTime(2026, 7, 14, 23, 40, 0), Box("GPMF", "DEVC"u8.ToArray(), new byte[12])), "GH010001.MP4");
        Assert.Equal(new DateTime(2026, 7, 14, 23, 40, 0), date.Local);
    }

    [Theory]
    [InlineData("1.00")]
    [InlineData("Ver.1.20")]
    [InlineData("HX")]
    [InlineData("")]
    public void OtherFirmwareStringsDoNotMakeAGoPro(string firmware)
    {
        var date = Read(GoProMp4(new DateTime(2026, 7, 14, 22, 40, 0), Box("FIRM", Encoding.ASCII.GetBytes(firmware))), "VID_0001.MP4");

        Assert.Equal(DateSource.MetadataGuessedZone, date.Source);
        Assert.Equal(new DateTime(2026, 7, 15, 0, 40, 0), date.Local);
    }

    // ---------------------------------------------------------------- AVI

    private static byte[] Chunk(string id, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(id), .. LittleEndian((uint)data.Length), .. data, .. (data.Length % 2 == 1 ? new byte[1] : [])];

    private static byte[] List(string type, params byte[][] chunks) => Chunk("LIST", [.. Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);

    private static byte[] LittleEndian(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private static byte[] Avi(params byte[][] chunks)
    {
        var body = chunks.SelectMany(c => c).ToArray();
        return [.. "RIFF"u8, .. LittleEndian((uint)(4 + body.Length)), .. "AVI "u8, .. body];
    }

    [Theory]
    [InlineData("Thu Jul 04 17:06:39 2002\n\0", 2002, 7, 4, 17, 6, 39)] // Canon compacts (C asctime)
    [InlineData("Sat Mar  2 23:59:01 2024\n\0", 2024, 3, 2, 23, 59, 1)]
    [InlineData("2012/05/27 16:11:38\0", 2012, 5, 27, 16, 11, 38)]     // Pentax
    [InlineData("2019:10:11 00:05:00", 2019, 10, 11, 0, 5, 0)]
    public void AviIditChunkGivesTheCameraClock(string idit, int y, int mo, int d, int h, int mi, int s)
    {
        var avi = Avi(List("hdrl", Chunk("avih", new byte[56]), List("strl", Chunk("strh", new byte[56])), Chunk("IDIT", Encoding.ASCII.GetBytes(idit))),
            Chunk("JUNK", new byte[100]), List("movi", Chunk("00dc", new byte[64])), Chunk("idx1", new byte[16]));

        var date = Read(avi, "MVI_0001.AVI", TimeZoneInfo.Utc);

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), date.Local);
    }

    [Fact]
    public void AviIditAtTheTopLevelIsFoundToo()
    {
        var avi = Avi(List("hdrl", Chunk("avih", new byte[56])), Chunk("IDIT", "2012/05/27 16:11:38\0"u8.ToArray()), List("movi"));
        Assert.Equal(new DateTime(2012, 5, 27, 16, 11, 38), Read(avi, "IMGP0001.AVI").Local);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("Thu Jan 01 00:00:00 1970\n\0")] // an unset clock
    [InlineData("2099/01/01 00:00:00")]          // in the future
    public void AviWithoutAUsableIditGetsTheFileTime(string idit)
    {
        var avi = Avi(List("hdrl", Chunk("avih", new byte[56]), Chunk("IDIT", Encoding.ASCII.GetBytes(idit))), List("movi"));
        Assert.Equal(DateSource.FileModified, Read(avi, "MVI_0001.AVI").Source);
    }

    [Fact]
    public void AnIditInsideTheVideoDataIsIgnored()
    {
        // Only the header lists are searched; frame data can contain anything.
        var avi = Avi(List("hdrl", Chunk("avih", new byte[56])), List("movi", Chunk("IDIT", "2012/05/27 16:11:38\0"u8.ToArray())));
        Assert.Equal(DateSource.FileModified, Read(avi, "MVI_0001.AVI").Source);
    }

    // ---------------------------------------------------------------- AVCHD

    private static readonly byte[] MdpmUuid = Convert.FromHexString("17EE8C60F84D11D98CD60800200C9A66");

    /// <summary>
    /// An .MTS: 192-byte packets (4-byte timestamp + 0x47 sync), the SEI with the MDPM block in the first video packet.
    /// The entries contain "00 00 03" emulation bytes, as in camera files.
    /// </summary>
    private static byte[] Mts(byte[] mdpmEntries, int count)
    {
        var sei = (byte[])[0x00, 0x00, 0x00, 0x01, 0x06, 0x05, 0x40, .. MdpmUuid, .. "MDPM"u8, (byte)count, .. mdpmEntries, 0x80];
        var stream = new List<byte>();
        for (var p = 0; p < 6; p++)
        {
            var packet = new byte[192];
            packet[4] = 0x47;
            packet[5] = 0x40;
            packet[6] = 0x11;
            if (p == 3) sei.AsSpan(0, Math.Min(sei.Length, 180)).CopyTo(packet.AsSpan(12));
            stream.AddRange(packet);
        }
        return [.. stream];
    }

    private static byte[] Entries(byte zone, int year, int month, int day, int hour, int minute, int second)
    {
        static byte Bcd(int v) => (byte)((v / 10 << 4) | (v % 10));
        return
        [
            0x13, 0x45, 0x09, 0x59, 0x01,
            0x14, 0x00, 0x00, 0x03, 0x00, 0x00,                                  // "00 00 00 00" with an emulation byte
            0x18, zone, Bcd(year / 100), Bcd(year % 100), Bcd(month),
            0x19, Bcd(day), Bcd(hour), Bcd(minute), Bcd(second),
            0x70, 0xFF, 0x02, 0x1F, 0x1D,
        ];
    }

    [Theory]
    [InlineData(2017, 3, 16, 10, 42, 32)]
    [InlineData(2008, 5, 29, 23, 59, 59)]
    [InlineData(2026, 12, 31, 0, 0, 0)]
    public void AvchdMdpmGivesTheCameraClock(int y, int mo, int d, int h, int mi, int s)
    {
        var date = Read(Mts(Entries(0x2A, y, mo, d, h, mi, s), 5), "00000.MTS", TimeZoneInfo.Utc);

        Assert.Equal(DateSource.Metadata, date.Source);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), date.Local);
    }

    [Fact]
    public void AvchdWithout188Or192BytePacketsIsNotSearched()
    {
        var mts = Mts(Entries(0x2A, 2017, 3, 16, 10, 42, 32), 5);
        mts[4] = 0x00; // no sync byte at 0 or 4: not a transport stream
        Assert.Equal(DateSource.FileModified, Read(mts, "00000.MTS").Source);
    }

    [Theory]
    [InlineData(2017, 13, 16, 10, 42, 32)] // month 13
    [InlineData(2017, 2, 30, 10, 42, 32)]  // 30 February
    [InlineData(2017, 3, 16, 24, 0, 0)]    // hour 24
    [InlineData(1980, 1, 1, 0, 0, 0)]      // an unset clock
    public void AvchdWithAnImpossibleDateGetsTheFileTime(int y, int mo, int d, int h, int mi, int s)
    {
        Assert.Equal(DateSource.FileModified, Read(Mts(Entries(0x2A, y, mo, d, h, mi, s), 5), "00000.MTS").Source);
    }

    [Fact]
    public void AvchdWithNonBcdDigitsOrMissingEntriesGetsTheFileTime()
    {
        var entries = Entries(0x2A, 2017, 3, 16, 10, 42, 32);
        var badBcd = (byte[])entries.Clone();
        badBcd[Array.IndexOf(badBcd, (byte)0x19) + 2] = 0x1A; // hour "1A"
        Assert.Equal(DateSource.FileModified, Read(Mts(badBcd, 5), "00000.MTS").Source);
        Assert.Equal(DateSource.FileModified, Read(Mts(entries, 3), "00000.MTS").Source); // count stops before 0x19
    }

    [Fact]
    public void DamagedLegacyContainersNeverThrow()
    {
        var samples = new[]
        {
            Mts(Entries(0x2A, 2017, 3, 16, 10, 42, 32), 5),
            Avi(List("hdrl", Chunk("avih", new byte[56]), Chunk("IDIT", "2012/05/27 16:11:38\0"u8.ToArray())), List("movi")),
        };
        var random = new Random(20261113);
        foreach (var original in samples)
        {
            for (var i = 0; i < 2000; i++)
            {
                var bytes = (byte[])original.Clone();
                for (var k = random.Next(1, 6); k > 0; k--) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
                if (random.Next(6) == 0) Array.Resize(ref bytes, random.Next(bytes.Length));
                using var s = new MemoryStream(bytes);
                Assert.Null(Record.Exception(() => LegacyVideoReader.ReadAviDate(s)));
                Assert.Null(Record.Exception(() => LegacyVideoReader.ReadAvchdDate(s)));
                var date = Read(bytes, "00000.MTS");
                Assert.True(date.Source is DateSource.Metadata or DateSource.FileModified);
            }
        }
    }
}
