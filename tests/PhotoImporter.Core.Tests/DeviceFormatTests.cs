using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Tests;

/// <summary>
/// Structural replicas of real device files (Fixtures/formats, see generate_fixtures.py): same layout as files
/// from a Canon camera and a Pixel phone, invented values. Times are chosen so that a wrong interpretation
/// lands in the neighbouring day.
/// </summary>
public class DeviceFormatTests
{
    [Theory]
    // Canon MP4: EXIF in the embedded thumbnail (camera clock 23:40, +01:00) wins over mvhd UTC (22:40 -> 00:40 in Berlin summer).
    [InlineData("VID_canon.MP4", "2026-07-14 23:40:00", DateSource.Metadata)]
    // Canon CR3: EXIF in moov/uuid/CMT2.
    [InlineData("IMG_canon.CR3", "2026-03-07 23:50:00", DateSource.Metadata)]
    // Pixel DNG (TIFF + EXIF).
    [InlineData("IMG_pixel.dng", "2026-05-03 06:10:00", DateSource.Metadata)]
    // Pixel MP4: mdat first, UTC 22:30 + GPS in Berlin -> 00:30 next day; Android keys are ignored.
    [InlineData("VID_pixel.mp4", "2026-05-03 00:30:00", DateSource.Metadata)]
    public void ReadsDeviceFormats(string file, string expected, DateSource source)
    {
        var path = Path.Combine(Fixtures.FormatsRoot, file);
        Assert.True(MediaTypes.TryGetKind(path, out var kind));

        var date = new CaptureDateReader(Fixtures.Berlin).Read(path, kind, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), date.Local);
        Assert.Equal(source, date.Source);
    }

    [Fact]
    public void EveryFormatFixtureHasAnExpectation()
    {
        var covered = typeof(DeviceFormatTests).GetMethod(nameof(ReadsDeviceFormats))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(Directory.GetFiles(Fixtures.FormatsRoot).Select(Path.GetFileName), f => Assert.Contains(f!, covered));
    }

    [Fact]
    public void CanonVideoUsesTheSameClockAsCanonPhotos()
    {
        // Without the embedded EXIF the same video would be dated by UTC + the computer's zone: the next day.
        var info = IsoBmffReader.Read(Path.Combine(Fixtures.FormatsRoot, "VID_canon.MP4"));
        Assert.NotNull(info.EmbeddedJpeg);
        var withoutExif = new CaptureDateReader(Fixtures.Berlin).ResolveVideoDate(info with { EmbeddedJpeg = null });
        Assert.Equal(new DateTime(2026, 7, 15, 0, 40, 0), withoutExif!.Value.Local);
        Assert.Equal(DateSource.MetadataGuessedZone, withoutExif.Value.Source);
    }

    [Fact]
    public void DamagedEmbeddedThumbnailFallsBackToContainerTime()
    {
        var info = IsoBmffReader.Read(Path.Combine(Fixtures.FormatsRoot, "VID_canon.MP4"));
        var broken = info with { EmbeddedJpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x10, 0x45] };
        var date = new CaptureDateReader(Fixtures.Berlin).ResolveVideoDate(broken);
        Assert.Equal(DateSource.MetadataGuessedZone, date!.Value.Source);
    }
}
