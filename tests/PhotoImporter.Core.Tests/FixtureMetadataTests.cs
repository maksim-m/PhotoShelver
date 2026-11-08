using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Tests;

/// <summary>Capture dates of the committed fixtures (see Fixtures/generate_fixtures.py).</summary>
public class FixtureMetadataTests
{
    private const string Mtime = "2025-12-31 13:00:00"; // FixtureMtimeUtc in Berlin

    [Theory]
    [InlineData(@"photos\IMG_0001.JPG", "2026-11-05 09:15:00", DateSource.Metadata)]
    [InlineData(@"photos\IMG_0002.JPG", "2026-11-05 23:59:59", DateSource.Metadata)] // OffsetTimeOriginal must not shift the date
    [InlineData(@"photos\IMG_0003.JPG", "2026-11-07 10:00:00", DateSource.Metadata)] // DateTimeDigitized only
    [InlineData(@"photos\IMG_0004.JPG", "2026-11-07 11:00:00", DateSource.Metadata)] // IFD0 DateTime only
    [InlineData(@"photos\IMG_0005.JPG", Mtime, DateSource.FileModified)]             // no EXIF
    [InlineData(@"photos\img_0006.jpeg", "2026-11-08 00:00:01", DateSource.Metadata)]
    [InlineData(@"photos\IMG_0007.PNG", Mtime, DateSource.FileModified)]
    [InlineData(@"photos\sub\IMG_0001_copy.JPG", "2026-11-05 09:15:00", DateSource.Metadata)]
    [InlineData(@"photos\BROKEN.JPG", Mtime, DateSource.FileModified)]
    [InlineData(@"photos\EMPTY.JPG", Mtime, DateSource.FileModified)]
    [InlineData(@"video\VID_apple.MOV", "2026-11-07 23:30:00", DateSource.Metadata)]        // Apple date with +0900
    [InlineData(@"video\VID_gps.MP4", "2026-11-08 07:30:00", DateSource.Metadata)]          // 22:30Z + Tokyo GPS
    [InlineData(@"video\VID_utc.MP4", "2026-01-16 00:30:00", DateSource.MetadataGuessedZone)] // 23:30Z, Berlin winter
    [InlineData(@"video\VID_v1.MP4", "2026-06-01 12:00:00", DateSource.MetadataGuessedZone)]  // mvhd v1 + largesize, Berlin summer
    [InlineData(@"video\VID_isometa.MP4", "2026-03-01 08:00:00", DateSource.Metadata)]       // ISO 'meta' full box
    [InlineData(@"video\VID_nodate.MP4", Mtime, DateSource.FileModified)]                     // mvhd time = 0
    [InlineData(@"video\VID_truncated.MP4", Mtime, DateSource.FileModified)]                  // moov runs past EOF
    public void ReadsExpectedCaptureDate(string relative, string expected, DateSource source)
    {
        using var dir = new TempDir();
        Fixtures.CopyTo(dir.Path);
        var path = System.IO.Path.Combine(dir.Path, relative);
        Assert.True(MediaTypes.TryGetKind(path, out var kind));

        var date = new CaptureDateReader(Fixtures.Berlin).Read(path, kind, File.GetLastWriteTimeUtc(path));

        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), date.Local);
        Assert.Equal(source, date.Source);
    }

    [Fact]
    public void EveryMediaFixtureHasAnExpectation()
    {
        // Guards against adding a fixture without asserting its date.
        var covered = typeof(FixtureMetadataTests).GetMethod(nameof(ReadsExpectedCaptureDate))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false).Cast<InlineDataAttribute>()
            .Select(a => (string)a.GetData(null!).First()[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var media = Directory.EnumerateFiles(Fixtures.MediaRoot, "*", SearchOption.AllDirectories)
            .Where(f => MediaTypes.TryGetKind(f, out _))
            .Select(f => System.IO.Path.GetRelativePath(Fixtures.MediaRoot, f));
        Assert.All(media, m => Assert.Contains(m, covered));
    }
}
