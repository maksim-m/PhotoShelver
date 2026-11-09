using PhotoShelver.Core.Metadata;

namespace PhotoShelver.Core.Tests;

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
    // Nikon NEF (big-endian TIFF), Sony ARW (little-endian TIFF), Fujifilm RAF (EXIF inside the embedded JPEG).
    [InlineData("IMG_nikon.NEF", "2026-08-20 23:55:00", DateSource.Metadata)]
    [InlineData("IMG_sony.ARW", "2026-09-01 00:05:00", DateSource.Metadata)]
    [InlineData("IMG_fuji.RAF", "2026-10-10 10:10:00", DateSource.Metadata)]
    // Film-scanner TIFF without any date: the file time (2000-01-01 00:00 UTC = 01:00 in Berlin).
    [InlineData("IMG_scan.tif", "2000-01-01 01:00:00", DateSource.FileModified)]
    public void ReadsDeviceFormats(string file, string expected, DateSource source)
    {
        var path = Path.Combine(Fixtures.FormatsRoot, file);
        Assert.True(MediaTypes.TryGetKind(path, out var kind));

        var date = new CaptureDateReader(Fixtures.Berlin).Read(path, kind, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), date.Local);
        Assert.Equal(source, date.Source);
    }

    [Theory]
    // The smallest preview that is still >= 320 px: never the 160x120 thumbnail, never lossless RAW data.
    [InlineData("IMG_nikon.NEF", 400, 300, 6)] // preview in a sub-IFD; a lossless "RAW" strip next to it must be ignored
    [InlineData("IMG_sony.ARW", 400, 300, 8)]  // preview referenced from IFD0
    [InlineData("IMG_canon.CR3", 400, 300, 8)] // PRVW wins over THMB; orientation from CMT1
    [InlineData("IMG_fuji.RAF", 400, 300, 3)]  // orientation from the embedded JPEG's EXIF
    public void ExtractsTheEmbeddedPreview(string file, int width, int height, int orientation)
    {
        var path = Path.Combine(Fixtures.FormatsRoot, file);
        Assert.True(EmbeddedPreview.IsRaw(path));

        var preview = EmbeddedPreview.TryRead(path);

        Assert.NotNull(preview);
        Assert.Equal((width, height), EmbeddedPreview.JpegSize(preview.Jpeg));
        Assert.Equal(orientation, preview.Orientation);
        Assert.Equal(0xD9, preview.Jpeg[^1]); // the whole JPEG, up to its end marker
    }

    [Fact]
    public void NoPreviewWhereThereIsNone()
    {
        Assert.Null(EmbeddedPreview.TryRead(Path.Combine(Fixtures.FormatsRoot, "IMG_pixel.dng"))); // TIFF without a JPEG preview
        Assert.Null(EmbeddedPreview.TryRead(Path.Combine(Fixtures.MediaRoot, @"photos\IMG_0001.JPG"))); // not a RAW container
        Assert.Null(EmbeddedPreview.TryRead(Path.Combine(Fixtures.MediaRoot, @"photos\EMPTY.JPG")));
        Assert.Null(EmbeddedPreview.TryRead(Path.Combine(Fixtures.MediaRoot, "does-not-exist.NEF")));
        Assert.False(EmbeddedPreview.IsRaw("IMG.JPG"));
        Assert.False(EmbeddedPreview.IsRaw("VID.MP4"));
    }

    [Fact]
    public void ReadsTiffSizeAndOrientation()
    {
        var path = Path.Combine(Fixtures.FormatsRoot, "IMG_scan.tif");
        Assert.True(EmbeddedPreview.IsTiff(path));
        Assert.Equal(new TiffImageInfo(240, 160, 8), EmbeddedPreview.TryReadTiffInfo(path));

        // Big-endian TIFF (NEF replica): orientation 6 from IFD0.
        Assert.Equal(6, EmbeddedPreview.TryReadTiffInfo(Path.Combine(Fixtures.FormatsRoot, "IMG_nikon.NEF"))!.Orientation);
        // A TIFF without the tag is upright.
        Assert.Equal(1, EmbeddedPreview.TryReadTiffInfo(Path.Combine(Fixtures.FormatsRoot, "IMG_pixel.dng"))!.Orientation);

        Assert.Null(EmbeddedPreview.TryReadTiffInfo(Path.Combine(Fixtures.MediaRoot, @"photos\IMG_0001.JPG")));
        Assert.Null(EmbeddedPreview.TryReadTiffInfo(Path.Combine(Fixtures.MediaRoot, @"photos\EMPTY.JPG")));
        Assert.Null(EmbeddedPreview.TryReadTiffInfo(Path.Combine(Fixtures.MediaRoot, "does-not-exist.tif")));
        Assert.False(EmbeddedPreview.IsTiff("IMG.NEF"));
        Assert.True(EmbeddedPreview.IsTiff("SCAN.TIFF"));
    }

    [Theory]
    // Quarter turns: applied only while the thumbnail still has the stored shape.
    [InlineData(8, 6774, 4492, 256, 170, 8)] // Windows ignored the tag (thumbnail landscape like the stored image)
    [InlineData(8, 6774, 4492, 170, 256, 1)] // Windows already turned it: never a second turn
    [InlineData(6, 4492, 6774, 170, 256, 6)] // stored portrait, thumbnail portrait
    [InlineData(6, 4492, 6774, 256, 170, 1)]
    [InlineData(5, 6774, 4492, 256, 170, 5)]
    [InlineData(7, 6774, 4492, 170, 256, 1)]
    // Shape tells nothing: the tag is applied.
    [InlineData(8, 3000, 3000, 256, 256, 8)]
    [InlineData(8, 6774, 4492, 256, 256, 8)]
    [InlineData(8, 0, 0, 256, 170, 8)]
    // 180 degrees and mirrors keep the shape: applied as is.
    [InlineData(3, 6774, 4492, 256, 170, 3)]
    [InlineData(2, 6774, 4492, 256, 170, 2)]
    [InlineData(4, 6774, 4492, 256, 170, 4)]
    // Upright or invalid: nothing to do.
    [InlineData(1, 6774, 4492, 256, 170, 1)]
    [InlineData(0, 6774, 4492, 256, 170, 1)]
    [InlineData(9, 6774, 4492, 256, 170, 1)]
    public void ThumbnailIsTurnedOnlyWhenWindowsDidNot(int orientation, int w, int h, int tw, int th, int expected)
    {
        Assert.Equal(expected, ThumbnailOrientation.ToApply(orientation, w, h, tw, th));
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
