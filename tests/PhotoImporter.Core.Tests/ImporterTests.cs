using PhotoImporter.Core.Importing;

namespace PhotoImporter.Core.Tests;

public class ImporterTests
{
    private static ImportJob Job(string src, string destFolder) => new(src, destFolder, new FileInfo(src).Length);

    [Fact]
    public void CopyKeepsSourceAndPreservesTimestamp()
    {
        using var dir = new TempDir();
        var mtime = new DateTime(2020, 5, 5, 12, 0, 0, DateTimeKind.Utc);
        var src = dir.File(@"src\IMG_1.JPG", "hello", mtime);
        var dest = System.IO.Path.Combine(dir.Path, @"dst\2020\05-05");

        var result = new Importer().Run([Job(src, dest)], ImportMode.Copy, null, default);

        Assert.Empty(result.Failures);
        var target = Assert.Single(result.Imported).DestinationPath;
        Assert.Equal(System.IO.Path.Combine(dest, "IMG_1.JPG"), target);
        Assert.Equal("hello", File.ReadAllText(target));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(target));
        Assert.True(File.Exists(src));
        Assert.Empty(Directory.GetFiles(dest, "*.importing"));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void NeverOverwritesAndAddsSuffix(ImportMode mode)
    {
        using var dir = new TempDir();
        var dest = dir.Sub("dst");
        dir.File(@"dst\IMG_1.JPG", "existing");
        dir.File(@"dst\IMG_1_1.JPG", "existing too");
        var src = dir.File(@"src\IMG_1.JPG", "new content");

        var result = new Importer().Run([Job(src, dest)], mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(System.IO.Path.Combine(dest, "IMG_1_2.JPG"), result.Imported[0].DestinationPath);
        Assert.Equal("existing", File.ReadAllText(System.IO.Path.Combine(dest, "IMG_1.JPG")));
        Assert.Equal("existing too", File.ReadAllText(System.IO.Path.Combine(dest, "IMG_1_1.JPG")));
        Assert.Equal("new content", File.ReadAllText(System.IO.Path.Combine(dest, "IMG_1_2.JPG")));
        Assert.Equal(mode == ImportMode.Copy, File.Exists(src));
    }

    [Fact]
    public void TwoSourcesWithSameNameGetDistinctNames()
    {
        using var dir = new TempDir();
        var dest = System.IO.Path.Combine(dir.Path, "dst");
        var a = dir.File(@"src\a\IMG.JPG", "A");
        var b = dir.File(@"src\b\IMG.JPG", "B");

        var result = new Importer().Run([Job(a, dest), Job(b, dest)], ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal("A", File.ReadAllText(System.IO.Path.Combine(dest, "IMG.JPG")));
        Assert.Equal("B", File.ReadAllText(System.IO.Path.Combine(dest, "IMG_1.JPG")));
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void MissingSourceIsReportedAndOthersContinue()
    {
        using var dir = new TempDir();
        var dest = System.IO.Path.Combine(dir.Path, "dst");
        var good = dir.File(@"src\good.jpg", "ok");

        var result = new Importer().Run(
            [new ImportJob(System.IO.Path.Combine(dir.Path, "missing.jpg"), dest, 10), Job(good, dest)],
            ImportMode.Copy, null, default);

        Assert.Single(result.Failures);
        Assert.Single(result.Imported);
        Assert.Empty(Directory.GetFiles(dest, "*.importing"));
    }

    [Fact]
    public void CancellationStopsBeforeNextFile()
    {
        using var dir = new TempDir();
        var dest = System.IO.Path.Combine(dir.Path, "dst");
        var a = dir.File(@"src\a.jpg", "a");
        var b = dir.File(@"src\b.jpg", "b");
        using var cts = new CancellationTokenSource();
        // Cancel while the first file is in progress: it must finish, the second must not start.
        var progress = new SyncProgress(p => { if (p.CurrentFile == a) cts.Cancel(); });

        var result = new Importer().Run([Job(a, dest), Job(b, dest)], ImportMode.Copy, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.Single(result.Imported);
    }

    private sealed class SyncProgress(Action<ImportProgress> action) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => action(value);
    }
}
