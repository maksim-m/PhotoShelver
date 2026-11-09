using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Metadata;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>Malformed input must never crash scanning: worst case is the file-time fallback.</summary>
public class RobustnessTests
{
    public static TheoryData<string> MediaFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var root in new[] { Fixtures.MediaRoot, Fixtures.FormatsRoot })
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                if (MediaTypes.TryGetKind(f, out _)) data.Add(Path.GetRelativePath(Fixtures.MediaRoot, f));
        return data;
    }

    [Theory]
    [MemberData(nameof(MediaFixtures))]
    public void TruncatedAndCorruptedFilesDoNotThrow(string relative)
    {
        using var dir = new TempDir();
        var original = File.ReadAllBytes(Path.Combine(Fixtures.MediaRoot, relative));
        MediaTypes.TryGetKind(relative, out var kind);
        var reader = new CaptureDateReader(Fixtures.Berlin);
        var path = Path.Combine(dir.Path, "probe" + Path.GetExtension(relative));
        var mtime = Fixtures.FixtureMtimeUtc;

        void Check(byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            var date = reader.Read(path, kind, mtime); // must not throw
            var preview = EmbeddedPreview.TryRead(path); // must not throw either
            if (preview is not null) Assert.Equal(0xFF, preview.Jpeg[0]);
            Assert.NotEqual(default, date.Local);
        }

        var lengths = Enumerable.Range(0, 20).Concat(Enumerable.Range(1, original.Length / 37).Select(i => i * 37)).Where(l => l < original.Length);
        foreach (var len in lengths) Check(original[..len]);

        var rnd = new Random(relative.GetHashCode(StringComparison.Ordinal));
        for (var i = 0; i < 60 && original.Length > 0; i++)
        {
            var corrupted = (byte[])original.Clone();
            for (var k = 0; k < 1 + i % 5; k++) corrupted[rnd.Next(corrupted.Length)] = (byte)rnd.Next(256);
            Check(corrupted);
        }
    }

    [Fact]
    public void IsoBmffReaderSurvivesRandomBytes()
    {
        var rnd = new Random(42);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = new byte[rnd.Next(0, 512)];
            rnd.NextBytes(bytes);
            // Make many inputs look like plausible boxes to reach deeper code paths.
            if (bytes.Length >= 8 && i % 2 == 0) "moov"u8.CopyTo(bytes.AsSpan(4));
            _ = IsoBmffReader.Read(new MemoryStream(bytes));
        }
    }

    [Fact]
    public async Task ScanOfFixturesHasNoErrors()
    {
        using var dir = new TempDir();
        Fixtures.CopyTo(dir.Path);
        var result = SourceScanner.Enumerate(dir.Path, recursive: true);
        await SourceScanner.ReadMetadataAsync(result.Files, new CaptureDateReader(Fixtures.Berlin), null, default);

        Assert.Equal(17, result.Files.Count);
        Assert.Equal(2, result.SkippedOther); // notes.txt, IMG_0001.XMP
        Assert.All(result.Files, f => Assert.Null(f.Error));
        Assert.All(result.Files, f => Assert.NotNull(f.Date));
    }

    [Fact]
    public void UnreadableTargetFileDoesNotBreakDuplicateCheck()
    {
        using var dir = new TempDir();
        var day = new DateTime(2026, 11, 8);
        var locked = dir.File(@"target\2026\11-08\locked.jpg", "SAME");
        var src = dir.File(@"src\a.jpg", "SAME");
        var fi = new FileInfo(src);
        var file = new SourceFile { Path = src, Size = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc, Kind = MediaKind.Photo, Date = new CaptureDate(day, DateSource.Metadata) };

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = DuplicateDetector.Detect([file], Path.Combine(dir.Path, "target"), FolderTemplate.Parse("YYYY/MM-DD"), new HashCache(), null, default);
            Assert.Equal(DuplicateState.New, result[file].State); // cannot prove it is a duplicate → treat as new
        }
    }

    [Fact]
    public void HashCacheNoticesModifiedFiles()
    {
        using var dir = new TempDir();
        var path = dir.File("a.jpg", "one", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cache = new HashCache();
        var first = cache.GetOrCompute(new FileInfo(path));

        File.WriteAllText(path, "two");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(first, cache.GetOrCompute(new FileInfo(path)));
    }
}

public class SourceScannerTests
{
    [Fact]
    public void FiltersByExtensionCaseInsensitively_AndCountsOthers()
    {
        using var dir = new TempDir();
        dir.File("a.JPG", "x");
        dir.File("b.jpeg", "x");
        dir.File("c.Mp4", "x");
        dir.File("d.xmp", "x");
        dir.File("e.txt", "x");

        var result = SourceScanner.Enumerate(dir.Path, recursive: false);

        Assert.Equal(["a.JPG", "b.jpeg", "c.Mp4"], result.Files.Select(f => f.FileName).Order());
        Assert.Equal(2, result.SkippedOther);
    }

    [Fact]
    public void RespectsRecursionFlag()
    {
        using var dir = new TempDir();
        dir.File("top.jpg", "x");
        dir.File(@"sub\deep\inner.jpg", "x");

        Assert.Single(SourceScanner.Enumerate(dir.Path, recursive: false).Files);
        Assert.Equal(2, SourceScanner.Enumerate(dir.Path, recursive: true).Files.Count);
    }

    [Fact]
    public void SkipsHiddenAndSystemFiles()
    {
        using var dir = new TempDir();
        dir.File("visible.jpg", "x");
        File.SetAttributes(dir.File("hidden.jpg", "x"), FileAttributes.Hidden);
        File.SetAttributes(dir.File("system.jpg", "x"), FileAttributes.System);

        var result = SourceScanner.Enumerate(dir.Path, recursive: true);

        Assert.Equal("visible.jpg", Assert.Single(result.Files).FileName);
    }
}
