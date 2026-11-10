using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>Full pipeline over the committed fixtures, as the app runs it.</summary>
public class EndToEndTests
{
    internal static readonly string[] ExpectedTree =
    [
        @"2025\12-31\BROKEN.JPG",
        @"2025\12-31\EMPTY.JPG",
        @"2025\12-31\IMG_0005.JPG",
        @"2025\12-31\IMG_0007.PNG",
        @"2025\12-31\VID_nodate.MP4",
        @"2025\12-31\VID_truncated.MP4",
        @"2026\01-16\VID_utc.MP4",
        @"2026\03-01\VID_isometa.MP4",
        @"2026\06-01\VID_v1.MP4",
        @"2026\11-05\IMG_0001.JPG",
        @"2026\11-05\IMG_0002.JPG",
        @"2026\11-07\IMG_0003.JPG",
        @"2026\11-07\IMG_0004.JPG",
        @"2026\11-07\VID_apple.MOV",
        @"2026\11-08\img_0006.jpeg",
        @"2026\11-08\VID_gps.MP4",
    ];

    private static (TempDir Dir, string Source, string Target) Setup()
    {
        var dir = new TempDir();
        var source = Path.Combine(dir.Path, "card");
        var target = Path.Combine(dir.Path, "library");
        Fixtures.CopyTo(source);
        Directory.CreateDirectory(target);
        return (dir, source, target);
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)] // cross-volume path: copy + verify + delete
    public async Task FreshImportBuildsExpectedTree(ImportMode mode, bool crossVolume)
    {
        var (dir, source, target) = Setup();
        using var _ = dir;
        var srcBefore = Snapshot.Take(source);
        var tgtBefore = Snapshot.Take(target);

        var outcome = await Pipeline.RunAsync(source, target, mode, importer: new Importer { TreatAsDifferentVolumes = crossVolume });

        Assert.Empty(outcome.Result.Failures);
        var tgtAfter = Snapshot.Take(target);
        var srcAfter = Snapshot.Take(source);
        Assert.Equal(ExpectedTree, tgtAfter.Paths);

        // Imported files are byte-identical to the originals.
        foreach (var imported in outcome.Result.Imported)
        {
            var original = srcBefore.Files[Path.GetRelativePath(source, imported.SourcePath)];
            Assert.Equal(original, tgtAfter.Files[Path.GetRelativePath(target, imported.DestinationPath)]);
        }

        if (mode == ImportMode.Copy)
        {
            Assert.Equal(srcBefore.Files, srcAfter.Files); // source untouched
        }
        else
        {
            // Only the source duplicate and non-media files stay behind.
            Assert.Equal([@"other\IMG_0001.XMP", @"other\notes.txt", @"photos\sub\IMG_0001_copy.JPG"], srcAfter.Paths);
        }

        Invariants.NothingLost([srcBefore, tgtBefore], [srcAfter, tgtAfter]);
        Invariants.NoTempFiles(target);
    }

    [Fact]
    public async Task SecondRunImportsNothing()
    {
        var (dir, source, target) = Setup();
        using var _ = dir;
        await Pipeline.RunAsync(source, target, ImportMode.Copy);
        var targetBefore = Snapshot.Take(target);

        var second = await Pipeline.RunAsync(source, target, ImportMode.Copy);

        Assert.Empty(second.Jobs);
        Assert.Equal(targetBefore.Files, Snapshot.Take(target).Files);
        Assert.All(second.Duplicates.Values, d => Assert.NotEqual(DuplicateState.New, d.State));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public async Task ExistingLibraryIsRespected(ImportMode mode)
    {
        var (dir, source, target) = Setup();
        using var _ = dir;
        // Same content as IMG_0001 under another name in the template folder → IMG_0001 (and its copy) are "already imported".
        Directory.CreateDirectory(Path.Combine(target, @"2026\11-05"));
        File.Copy(Path.Combine(source, @"photos\IMG_0001.JPG"), Path.Combine(target, @"2026\11-05\old-name.jpg"));
        // Unrelated file with a clashing name → the new IMG_0003 must become IMG_0003_1.
        Directory.CreateDirectory(Path.Combine(target, @"2026\11-07"));
        File.WriteAllText(Path.Combine(target, @"2026\11-07\IMG_0003.JPG"), "unrelated");
        // Same content as img_0006 but in a different day folder → not considered (SPEC §5), so it is imported.
        Directory.CreateDirectory(Path.Combine(target, @"2026\11-09"));
        File.Copy(Path.Combine(source, @"photos\img_0006.jpeg"), Path.Combine(target, @"2026\11-09\img_0006.jpeg"));

        var srcBefore = Snapshot.Take(source);
        var tgtBefore = Snapshot.Take(target);

        var outcome = await Pipeline.RunAsync(source, target, mode);

        Assert.Empty(outcome.Result.Failures);
        var byName = outcome.Files.ToDictionary(f => Path.GetRelativePath(source, f.Path), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(DuplicateState.InTarget, outcome.Duplicates[byName[@"photos\IMG_0001.JPG"]].State);
        Assert.Equal(DuplicateState.InTarget, outcome.Duplicates[byName[@"photos\sub\IMG_0001_copy.JPG"]].State);
        Assert.Equal(DuplicateState.New, outcome.Duplicates[byName[@"photos\img_0006.jpeg"]].State);

        var tgtAfter = Snapshot.Take(target);
        Assert.Contains(@"2026\11-07\IMG_0003_1.JPG", tgtAfter.Paths);
        Assert.Contains(@"2026\11-08\img_0006.jpeg", tgtAfter.Paths);
        Assert.DoesNotContain(@"2026\11-05\IMG_0001.JPG", tgtAfter.Paths);
        Assert.Equal(srcBefore.Files[@"photos\IMG_0003.JPG"], tgtAfter.Files[@"2026\11-07\IMG_0003_1.JPG"]);

        // Files already imported are never touched in the source, even in move mode.
        var srcAfter = Snapshot.Take(source);
        Assert.Contains(@"photos\IMG_0001.JPG", srcAfter.Paths);
        Assert.Contains(@"photos\sub\IMG_0001_copy.JPG", srcAfter.Paths);

        Invariants.TargetFilesUnchanged(tgtBefore, tgtAfter);
        Invariants.NothingLost([srcBefore, tgtBefore], [srcAfter, tgtAfter]);
        Invariants.NoTempFiles(target);
    }

    [Fact]
    public async Task DifferentTemplateBuildsDifferentTree()
    {
        var (dir, source, target) = Setup();
        using var _ = dir;

        await Pipeline.RunAsync(source, target, ImportMode.Copy, template: @"Photos {YYYY}\{MMM}");

        var tree = Snapshot.Take(target).Paths.Select(p => Path.GetDirectoryName(p)!).Distinct().ToList();
        Assert.Equal([@"Photos 2025\Dec", @"Photos 2026\Jan", @"Photos 2026\Jun", @"Photos 2026\Mar", @"Photos 2026\Nov"], tree);
    }
}
