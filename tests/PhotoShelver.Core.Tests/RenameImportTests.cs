using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>Import with a file name template: files get new names, and every safety rule still holds.</summary>
public class RenameImportTests
{
    private static readonly DateTime Shot = new(2026, 8, 14, 7, 12, 5);

    private static SourceFile File(string path, DateTime? local = null) => new()
    {
        Path = path,
        Size = new FileInfo(path).Length,
        LastWriteTimeUtc = System.IO.File.GetLastWriteTimeUtc(path),
        Kind = MediaKind.Photo,
        Date = new CaptureDate(local ?? Shot, DateSource.Metadata),
    };

    private static List<ImportJob> Plan(string target, string names, params SourceFile[] files) =>
        ImportPlan.CreateJobs(files, target, FolderTemplate.Parse("{YYYY}/{YYYY}-{MM}-{DD}"), FileNameTemplate.Parse(names));

    [Fact]
    public void PlanNamesFilesByTheTemplate()
    {
        using var dir = new TempDir();
        var a = File(dir.File(@"card\IMG_2041.JPG", "a"));
        var jobs = Plan(dir.Sub("lib"), "{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}", a);

        var job = Assert.Single(jobs);
        Assert.Equal("20260814_071205_IMG_2041.JPG", job.FileName);
        Assert.Equal(Path.Combine(dir.Path, "lib", "2026", "2026-08-14"), job.DestinationFolder);
    }

    [Fact]
    public void WithoutATemplateOrWithNameTheOriginalNameIsKept()
    {
        using var dir = new TempDir();
        var a = File(dir.File(@"card\IMG_2041.JPG", "a"));
        var tpl = FolderTemplate.Parse("{YYYY}");

        Assert.Equal("IMG_2041.JPG", ImportPlan.CreateJobs([a], dir.Path, tpl).Single().FileName);
        Assert.Equal("IMG_2041.JPG", Plan(dir.Path, "{NAME}", a).Single().FileName);
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]  // same volume: atomic rename
    [InlineData(ImportMode.Move, true)]   // other volume: copy, verify, delete
    public void FilesAreImportedUnderTheirNewNames(ImportMode mode, bool otherVolume)
    {
        using var dir = new TempDir();
        var src = dir.File(@"card\IMG_2041.JPG", "photo");
        var jobs = Plan(dir.Sub("lib"), "{YYYY}-{MM}-{DD}_{NAME}", File(src));

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(jobs, mode, null, default);

        Assert.Empty(result.Failures);
        var placed = Path.Combine(dir.Path, "lib", "2026", "2026-08-14", "2026-08-14_IMG_2041.JPG");
        Assert.Equal(placed, Assert.Single(result.Imported).DestinationPath);
        Assert.Equal("photo", System.IO.File.ReadAllText(placed));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(placed)!)); // nothing under the old name, no temp file
        Assert.Equal(mode == ImportMode.Copy, System.IO.File.Exists(src));
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)]
    public void ANewNameNeverOverwritesAnExistingFile(ImportMode mode, bool otherVolume)
    {
        using var dir = new TempDir();
        var folder = dir.Sub("lib", "2026", "2026-08-14");
        dir.File(@"lib\2026\2026-08-14\2026-08-14_07-12-05.JPG", "already in the library");
        var src = dir.File(@"card\IMG_2041.JPG", "new shot");
        var jobs = Plan(Path.Combine(dir.Path, "lib"), "{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", File(src));

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(jobs, mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(folder, "2026-08-14_07-12-05_1.JPG"), result.Imported.Single().DestinationPath);
        Assert.Equal("already in the library", System.IO.File.ReadAllText(Path.Combine(folder, "2026-08-14_07-12-05.JPG")));
        Assert.Equal("new shot", System.IO.File.ReadAllText(Path.Combine(folder, "2026-08-14_07-12-05_1.JPG")));
    }

    [Fact]
    public void ABurstInOneSecondGetsDistinctNames()
    {
        using var dir = new TempDir();
        var files = Enumerable.Range(1, 3).Select(i => File(dir.File($@"card\IMG_{i}.JPG", $"frame {i}"))).ToArray();
        var jobs = Plan(dir.Sub("lib"), "{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", files);

        var result = new Importer().Run(jobs, ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(["2026-08-14_07-12-05.JPG", "2026-08-14_07-12-05_1.JPG", "2026-08-14_07-12-05_2.JPG"],
            result.Imported.Select(i => Path.GetFileName(i.DestinationPath)));
        Assert.Equal(["frame 1", "frame 2", "frame 3"], result.Imported.Select(i => System.IO.File.ReadAllText(i.DestinationPath)));
    }

    [Fact]
    public void RawPlusJpegAndLivePhotoPairsStayPairedWithName()
    {
        using var dir = new TempDir();
        var files = new[]
        {
            File(dir.File(@"card\IMG_0001.CR3", "raw")), File(dir.File(@"card\IMG_0001.JPG", "jpeg")),
            File(dir.File(@"card\IMG_0002.HEIC", "still")), File(dir.File(@"card\IMG_0002.MOV", "motion")),
        };
        var jobs = Plan(dir.Sub("lib"), "{YYYY}-{MM}-{DD}_{NAME}", files);

        var result = new Importer().Run(jobs, ImportMode.Copy, null, default);

        Assert.Empty(result.Failures);
        var names = result.Imported.Select(i => Path.GetFileName(i.DestinationPath)).ToList();
        Assert.Equal(["2026-08-14_IMG_0001.CR3", "2026-08-14_IMG_0001.JPG", "2026-08-14_IMG_0002.HEIC", "2026-08-14_IMG_0002.MOV"], names);
    }

    [Theory]
    [InlineData(@"..\escape.JPG")]
    [InlineData(@"sub\x.JPG")]
    [InlineData("a:b.JPG")]
    [InlineData("")]
    [InlineData("x.")]
    [InlineData(" x.JPG")]
    [InlineData("NUL.JPG")]
    [InlineData("con")]
    [InlineData("COM1.tar.gz")]
    public void AnInvalidDestinationNameImportsNothingAndKeepsTheSource(string badName)
    {
        // Defence in depth: the planner never makes such names, but the importer refuses them on its own.
        using var dir = new TempDir();
        var src = dir.File(@"card\IMG_1.JPG", "photo");
        var dest = dir.Sub("lib");
        var job = new ImportJob(src, dest, new FileInfo(src).Length, badName);

        var result = new Importer { TreatAsDifferentVolumes = true }.Run([job], ImportMode.Move, null, default);

        Assert.Empty(result.Imported);
        Assert.Single(result.Failures);
        Assert.Equal("photo", System.IO.File.ReadAllText(src));
        Assert.DoesNotContain(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories), f => f != src);
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)]
    public void ANameTooLongForTheFileSystemImportsNothingAndKeepsTheSource(ImportMode mode, bool otherVolume)
    {
        // A valid template with a long original name can exceed 255 characters: the file fails, nothing is lost.
        using var dir = new TempDir();
        var src = dir.File($@"card\{new string('n', 120)}.JPG", "photo");
        var jobs = Plan(dir.Sub("lib"), "{NAME}_" + new string('x', 140), File(src));
        Assert.True(jobs[0].FileName.Length > 255);

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(jobs, mode, null, default);

        Assert.Empty(result.Imported);
        Assert.Single(result.Failures);
        Assert.Equal("photo", System.IO.File.ReadAllText(src));
        Assert.DoesNotContain(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories), f => f != src);
    }

    [Fact]
    public async Task EndToEndRenamedImportLosesNothing()
    {
        using var dir = new TempDir();
        var source = dir.Sub("card");
        Fixtures.CopyTo(source);
        var target = dir.Sub("lib");
        var before = new[] { Snapshot.Take(source), Snapshot.Take(target) };

        var outcome = await Pipeline.RunAsync(source, target, ImportMode.Move, names: "{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}",
            importer: new Importer { TreatAsDifferentVolumes = true });

        Assert.NotEmpty(outcome.Result.Imported);
        Invariants.NothingLost(before, [Snapshot.Take(source), Snapshot.Take(target)]);
        Invariants.NoTempFiles(target);
        foreach (var imported in outcome.Result.Imported)
        {
            var job = outcome.Jobs.Single(j => j.SourcePath == imported.SourcePath);
            Assert.Equal(job.FileName, Path.GetFileName(imported.DestinationPath));
            Assert.EndsWith("_" + Path.GetFileName(imported.SourcePath), job.FileName);
        }

        // The same card again: the renamed files are recognised by content as already imported.
        var card2 = dir.Sub("card2");
        Fixtures.CopyTo(card2);
        var rerun = await Pipeline.RunAsync(card2, target, ImportMode.Copy, names: "{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}");
        Assert.Empty(rerun.Jobs);
    }

    [Fact]
    public void LogShowsTheNameTemplate()
    {
        using var dir = new TempDir();
        var log = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, "s", "t", "YYYY", [], "1",
            nameTemplate: "{YYYY}-{MM}-{DD}_{NAME}");
        Assert.Contains("Имена:    {YYYY}-{MM}-{DD}_{NAME}", System.IO.File.ReadAllLines(log.Path));

        var plain = ImportLog.Start(Path.Combine(dir.Path, "logs2"), ImportMode.Copy, "s", "t", "YYYY", [], "1");
        Assert.DoesNotContain(System.IO.File.ReadAllLines(plain.Path), l => l.StartsWith("Имена", StringComparison.Ordinal));
    }
}
