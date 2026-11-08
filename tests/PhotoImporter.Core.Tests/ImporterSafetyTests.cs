using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Importing;

namespace PhotoImporter.Core.Tests;

/// <summary>
/// Fault injection: whatever goes wrong, a source file is deleted only after an identical copy exists in the target,
/// and nothing in the target is ever overwritten.
/// </summary>
public class ImporterSafetyTests
{
    private static ImportJob Job(string src, string destFolder) => new(src, destFolder, new FileInfo(src).Length);

    [Fact]
    public void CorruptedCopyIsDetected_SourceKept_NothingLeftBehind()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "original bytes");
        var dest = Path.Combine(dir.Path, "dst");
        var importer = new Importer
        {
            TreatAsDifferentVolumes = true,
            AfterTempWritten = temp => File.AppendAllText(temp, "bit rot"),
        };

        var result = importer.Run([Job(src, dest)], ImportMode.Move, null, default);

        Assert.Empty(result.Imported);
        Assert.Contains("хеш", Assert.Single(result.Failures).Message);
        Assert.Equal("original bytes", File.ReadAllText(src));
        Assert.Empty(Directory.GetFiles(dest));
    }

    [Theory]
    [InlineData(true)]  // content appended
    [InlineData(false)] // only the timestamp changed
    public void SourceChangedDuringCopyIsNotDeleted(bool changeContent)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\VID.MP4", "frame-1", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var dest = Path.Combine(dir.Path, "dst");
        var importer = new Importer
        {
            TreatAsDifferentVolumes = true,
            AfterTempWritten = _ =>
            {
                if (changeContent) File.AppendAllText(src, "frame-2");
                File.SetLastWriteTimeUtc(src, new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            },
        };

        var result = importer.Run([Job(src, dest)], ImportMode.Move, null, default);

        Assert.Single(result.Imported);
        Assert.Contains("изменился", Assert.Single(result.Failures).Message);
        Assert.True(File.Exists(src));
        Assert.Equal("frame-1", File.ReadAllText(Path.Combine(dest, "VID.MP4")));
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)]
    public void LockedSourceFails_SourceIntact(ImportMode mode, bool crossVolume)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var dest = Path.Combine(dir.Path, "dst");

        ImportResult result;
        using (new FileStream(src, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = new Importer { TreatAsDifferentVolumes = crossVolume }.Run([Job(src, dest)], mode, null, default);

        Assert.Empty(result.Imported);
        Assert.Single(result.Failures);
        Assert.Equal("data", File.ReadAllText(src));
        Invariants.NoTempFiles(dest);
        Assert.False(Directory.Exists(dest) && Directory.GetFiles(dest).Length > 0);
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void DestinationFolderBlockedByFile_SourceIntact(ImportMode mode)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var blocker = dir.File("dst", "I am a file, not a folder");

        var result = new Importer().Run([Job(src, blocker)], mode, null, default);

        Assert.Single(result.Failures);
        Assert.Equal("data", File.ReadAllText(src));
        Assert.Equal("I am a file, not a folder", File.ReadAllText(blocker));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void SourceAlreadyInDestinationFolderIsRefused(ImportMode mode)
    {
        using var dir = new TempDir();
        var folder = dir.Sub("2026", "11-08");
        var src = dir.File(@"2026\11-08\IMG.JPG", "data");

        var result = new Importer().Run([Job(src, folder)], mode, null, default);

        Assert.Single(result.Failures);
        Assert.Equal(["IMG.JPG"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public void ReadOnlySourceOnCrossVolumeMoveIsCopiedButKept()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        File.SetAttributes(src, FileAttributes.ReadOnly);
        var dest = Path.Combine(dir.Path, "dst");

        var result = new Importer { TreatAsDifferentVolumes = true }.Run([Job(src, dest)], ImportMode.Move, null, default);

        Assert.Single(result.Imported);
        Assert.Single(result.Failures);
        Assert.True(File.Exists(src));
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "IMG.JPG")));
        File.SetAttributes(src, FileAttributes.Normal);
    }

    [Fact]
    public void UnexpectedErrorsAreReportedPerFile_AndDoNotStopTheImport()
    {
        using var dir = new TempDir();
        var good = dir.File(@"src\good.jpg", "ok");
        var dest = Path.Combine(dir.Path, "dst");
        var jobs = new[]
        {
            new ImportJob("C:\\bad\0name.jpg", dest, 1),          // ArgumentException from the file system API
            new ImportJob(Path.Combine(dir.Path, "missing.jpg"), dest, 1),
            new ImportJob(good, "Z:\\\0invalid", 2),               // invalid destination
            Job(good, dest),
        };

        var result = new Importer().Run(jobs, ImportMode.Move, null, default);

        Assert.Equal(3, result.Failures.Count);
        Assert.Equal("ok", File.ReadAllText(Assert.Single(result.Imported).DestinationPath));
    }

    [Fact]
    public void CopyModeNeverDeletesAnything()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "dst");
        var ok = dir.File(@"src\ok.jpg", "1");
        var locked = dir.File(@"src\locked.jpg", "2");
        var clash = dir.File(@"src\clash.jpg", "3");
        dir.File(@"dst\clash.jpg", "existing");
        var before = Snapshot.Take(Path.Combine(dir.Path, "src"));

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            new Importer().Run([Job(ok, dest), Job(locked, dest), Job(clash, dest)], ImportMode.Copy, null, default);

        Assert.Equal(before.Files, Snapshot.Take(Path.Combine(dir.Path, "src")).Files);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(dest, "clash.jpg")));
        Assert.Equal("3", File.ReadAllText(Path.Combine(dest, "clash_1.jpg")));
    }

    [Fact]
    public void CancelledImportLeavesNoPartialFiles()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "dst");
        var big = dir.File(@"src\big.mp4", new string('v', 5_000_000));
        var small = dir.File(@"src\small.jpg", "s");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.CurrentFile == big) cts.Cancel(); }); // cancel while the big file is being copied

        var result = new Importer { TreatAsDifferentVolumes = true }.Run([Job(big, dest), Job(small, dest)], ImportMode.Move, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(5_000_000, new FileInfo(Path.Combine(dest, "big.mp4")).Length); // current file finished completely
        Assert.True(File.Exists(small));                                             // next one not started
        Invariants.NoTempFiles(dest);
    }

    /// <summary>Randomized scenarios: name clashes, identical contents, both modes, same/cross volume.</summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomizedImportsPreserveEverything(int seed)
    {
        var rnd = new Random(seed);
        using var dir = new TempDir();
        var srcRoot = Path.Combine(dir.Path, "src");
        var dstRoot = Path.Combine(dir.Path, "dst");
        string[] names = ["A.JPG", "B.JPG", "a.jpg", "C.MP4"];
        string[] folders = [@"2026\11-07", @"2026\11-08"];
        string RandomContent() => "content-" + rnd.Next(6); // small pool → identical contents happen

        var jobs = new List<ImportJob>();
        for (var i = 0; i < 25; i++)
        {
            var src = dir.File(Path.Combine("src", "d" + rnd.Next(4), $"{i:D2}_" + names[rnd.Next(names.Length)]), RandomContent());
            jobs.Add(Job(src, Path.Combine(dstRoot, folders[rnd.Next(folders.Length)])));
        }
        for (var i = 0; i < 6; i++)
        {
            // Pre-existing files that clash with the names the jobs will produce.
            var job = jobs[rnd.Next(jobs.Count)];
            var path = Path.Combine(job.DestinationFolder, Path.GetFileName(job.SourcePath));
            if (!File.Exists(path)) dir.File(Path.GetRelativePath(dir.Path, path), RandomContent());
        }

        var mode = rnd.Next(2) == 0 ? ImportMode.Copy : ImportMode.Move;
        var crossVolume = rnd.Next(2) == 0;
        var srcBefore = Snapshot.Take(srcRoot);
        var dstBefore = Snapshot.Take(dstRoot);
        var sourceHashes = jobs.ToDictionary(j => j.SourcePath, j => FileHasher.Hash(j.SourcePath));

        var result = new Importer { TreatAsDifferentVolumes = crossVolume }.Run(jobs, mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(jobs.Count, result.Imported.Count);
        Assert.Equal(jobs.Count, result.Imported.Select(i => i.DestinationPath.ToUpperInvariant()).Distinct().Count());
        foreach (var imported in result.Imported)
            Assert.Equal(sourceHashes[imported.SourcePath], FileHasher.Hash(imported.DestinationPath));
        Assert.All(jobs, j => Assert.Equal(mode == ImportMode.Copy, File.Exists(j.SourcePath)));

        var srcAfter = Snapshot.Take(srcRoot);
        var dstAfter = Snapshot.Take(dstRoot);
        Invariants.TargetFilesUnchanged(dstBefore, dstAfter);
        Invariants.NothingLost([srcBefore, dstBefore], [srcAfter, dstAfter]);
        Invariants.NoTempFiles(dstRoot);
    }

    public static TheoryData<int> Seeds() => [.. Enumerable.Range(1, 30)];

    private sealed class SyncProgress(Action<ImportProgress> action) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => action(value);
    }
}
