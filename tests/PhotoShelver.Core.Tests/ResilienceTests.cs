using PhotoShelver.Core.Importing;

namespace PhotoShelver.Core.Tests;

/// <summary>Antivirus-style transient locks, temp-name collisions, weaker verification and leftover cleanup.</summary>
public class ResilienceTests
{
    private static ImportJob Job(string src, string destFolder) => new(src, destFolder, new FileInfo(src).Length);

    private static readonly TimeSpan[] Fast = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];

    /// <summary>Holds a file open without delete sharing (like an antivirus scan) and releases it after a while.</summary>
    /// <remarks>
    /// Released by a thread of its own, not by a thread-pool timer: with other tests running in parallel on a machine
    /// with few cores (CI), the pool can be busy for seconds, the lock then outlasts the importer's ~3.5 s of retries
    /// and the test fails although the importer did everything right.
    /// </remarks>
    private static void LockBriefly(string path, int milliseconds)
    {
        var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        new Thread(() =>
        {
            Thread.Sleep(milliseconds);
            handle.Dispose();
        }) { IsBackground = true, Name = "brief lock" }.Start();
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)] // temp file → final name is blocked
    [InlineData(ImportMode.Move, false)] // same-volume rename of the source is blocked
    [InlineData(ImportMode.Move, true)]  // temp rename, then deleting the source
    public void BriefLockByAnotherProgramIsRetried(ImportMode mode, bool crossVolume)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var dest = Path.Combine(dir.Path, "dst");
        var importer = new Importer
        {
            TreatAsDifferentVolumes = crossVolume,
            TempFileName = name => name + ".0badc0de.importing",
            BeforePlace = _ =>
            {
                var temp = Path.Combine(dest, "IMG.JPG.0badc0de.importing");
                LockBriefly(File.Exists(temp) ? temp : src, 400);
            },
        };

        var result = importer.Run([Job(src, dest)], mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "IMG.JPG")));
        Assert.Equal(mode == ImportMode.Copy, File.Exists(src));
        Invariants.NoTempFiles(dest);
    }

    [Fact]
    public void PermanentLockFailsCleanly()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var dest = Path.Combine(dir.Path, "dst");

        ImportResult result;
        using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read)) // never released during the import
            result = new Importer { RetryDelays = Fast }.Run([Job(src, dest)], ImportMode.Move, null, default);

        Assert.Single(result.Failures);
        Assert.Equal("data", File.ReadAllText(src));
        Assert.False(File.Exists(Path.Combine(dest, "IMG.JPG")));
    }

    [Fact]
    public void TakenTempNameIsSkippedAndTheExistingFileKept()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "mine");
        var dest = Path.Combine(dir.Path, "dst");
        var foreign = dir.File(@"dst\IMG.JPG.aaaaaaaa.importing", "leftover from before");
        var calls = 0;
        var importer = new Importer { TempFileName = name => calls++ == 0 ? name + ".aaaaaaaa.importing" : name + ".bbbbbbbb.importing" };

        var result = importer.Run([Job(src, dest)], ImportMode.Copy, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "IMG.JPG")));
        Assert.Equal("leftover from before", File.ReadAllText(foreign));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void TempNameIsShort()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "x");
        var dest = Path.Combine(dir.Path, "dst");
        string? seen = null;
        var importer = new Importer { AfterTempWritten = t => seen = Path.GetFileName(t) };

        importer.Run([Job(src, dest)], ImportMode.Copy, null, default);

        Assert.Matches(@"^IMG\.JPG\.[0-9a-f]{8}\.importing$", seen);
        Assert.True(ImportLeftovers.IsLeftover(seen!));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void WeakerVerificationIsReported_AndMoveKeepsTheSource(ImportMode mode)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var dest = Path.Combine(dir.Path, "dst");

        var result = new Importer { TreatAsDifferentVolumes = true, ForceCachedVerification = true }
            .Run([Job(src, dest)], mode, null, default);

        Assert.Single(result.Imported);
        Assert.Equal("data", File.ReadAllText(Path.Combine(dest, "IMG.JPG")));
        Assert.Contains("кэша", Assert.Single(result.Warnings));
        Assert.True(File.Exists(src)); // in move mode too: no reliable re-read → no deletion
        if (mode == ImportMode.Move) Assert.Contains("не удалён", Assert.Single(result.Failures).Message);
        else Assert.Empty(result.Failures);
    }

    [Fact]
    public void NormalImportHasNoWarnings()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "data");
        var result = new Importer { TreatAsDifferentVolumes = true }.Run([Job(src, Path.Combine(dir.Path, "dst"))], ImportMode.Move, null, default);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void LeftoversAreFoundByPatternOnly()
    {
        using var dir = new TempDir();
        var a = dir.File(@"2026\2026-11-05\IMG_1.JPG.0123abcd.importing", "x");
        var b = dir.File(@"2026\2026-11-06\VID.MP4.0123456789abcdef0123456789abcdef.importing", "x"); // older builds
        dir.File(@"2026\2026-11-05\notes.importing", "user file");               // no id → not ours
        dir.File(@"2026\2026-11-05\IMG_2.JPG.xyz12345.importing", "user file");  // not hex → not ours
        dir.File(@"2026\2026-11-05\IMG_1.JPG", "photo");

        var found = ImportLeftovers.Find(dir.Path).Select(f => f.FullName).Order().ToList();

        Assert.Equal(new[] { a, b }.Order(), found);
    }

    [Fact]
    public void LeftoverDeletionRefusesAnythingElse()
    {
        using var dir = new TempDir();
        var leftover = dir.File(@"IMG_1.JPG.0123abcd.importing", "x");
        var photo = dir.File(@"IMG_1.JPG", "photo");

        var failed = ImportLeftovers.Delete([leftover, photo]);

        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(photo));
        Assert.Equal(photo, Assert.Single(failed).Path);
    }
}
