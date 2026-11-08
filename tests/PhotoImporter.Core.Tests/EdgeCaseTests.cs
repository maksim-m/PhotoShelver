using System.Diagnostics;
using PhotoImporter.Core.Importing;
using PhotoImporter.Core.Scanning;

namespace PhotoImporter.Core.Tests;

/// <summary>Edge cases found in the full logic review.</summary>
public class EdgeCaseTests
{
    private static SourceFile Photo(string path, DateTime date) => new()
    {
        Path = path, Size = 1, LastWriteTimeUtc = DateTime.UtcNow, Kind = MediaKind.Photo,
        Date = new CaptureDate(date, DateSource.Metadata),
    };

    // ---- Target / source at the root of a drive (e.g. a dedicated photo disk "E:\") ----

    [Theory]
    [InlineData(@"E:\")]
    [InlineData(@"E:")]
    public void JobsCanTargetADriveRoot(string root)
    {
        var job = Assert.Single(ImportPlan.CreateJobs(
            [Photo(@"F:\DCIM\IMG.JPG", new DateTime(2026, 3, 7))], root, FolderTemplate.Parse("YYYY/YYYY-MM-DD")));
        Assert.Equal(@"E:\2026\2026-03-07", job.DestinationFolder);
    }

    [Theory]
    [InlineData(@"E:\", @"E:\DCIM", true)]
    [InlineData(@"E:\DCIM", @"E:\", true)]
    [InlineData(@"E:\", @"E:\", true)]
    [InlineData(@"E:\", @"F:\DCIM", false)]
    [InlineData(@"E:\Photos\", @"E:\Photos", true)]
    [InlineData(@"E:\Photos", @"E:\Photos2", false)]
    public void NestingWorksWithDriveRootsAndTrailingSlashes(string a, string b, bool nested)
    {
        Assert.Equal(nested, PathUtil.AreNestedOrEqual(a, b));
    }

    [Fact]
    public void SourceAlreadyInDestinationIsRefusedEvenWithTrailingSlash()
    {
        using var dir = new TempDir();
        var folder = dir.Sub("2026", "2026-03-07");
        var src = dir.File(@"2026\2026-03-07\IMG.JPG", "data");

        var result = new Importer().Run([new ImportJob(src, folder + "\\", 4)], ImportMode.Move, null, default);

        Assert.Single(result.Failures);
        Assert.Equal(["IMG.JPG"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    // ---- Duplicate check and import agree on the destination folder ----

    [Fact]
    public void DuplicateCheckLooksInTheSameFolderTheImportWritesTo()
    {
        using var dir = new TempDir();
        var root = dir.Path + "\\"; // trailing slash must not change anything
        var template = FolderTemplate.Parse("YYYY/YYYY-MM-DD");
        var src = dir.File(@"card\IMG.JPG", "same");
        var file = new SourceFile
        {
            Path = src, Size = new FileInfo(src).Length, LastWriteTimeUtc = File.GetLastWriteTimeUtc(src), Kind = MediaKind.Photo,
            Date = new CaptureDate(new DateTime(2026, 3, 7), DateSource.Metadata),
        };
        var job = Assert.Single(ImportPlan.CreateJobs([file], root, template));
        Directory.CreateDirectory(job.DestinationFolder);
        File.WriteAllText(Path.Combine(job.DestinationFolder, "old-name.jpg"), "same");

        var result = DuplicateDetector.Detect([file], root, template, new Hashing.HashCache(), null, default);

        Assert.Equal(DuplicateState.InTarget, result[file].State);
    }

    // ---- Source with a junction loop (folder link pointing to its parent) ----

    [Fact]
    public void JunctionLoopInSourceDoesNotHangTheScan()
    {
        using var dir = new TempDir();
        dir.File(@"card\DCIM\IMG.JPG", "x");
        var loop = Path.Combine(dir.Path, @"card\DCIM\loop");
        var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{loop}\" \"{Path.Combine(dir.Path, "card")}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        mklink.WaitForExit();
        Assert.True(Directory.Exists(loop), "junction was not created");

        var task = Task.Run(() => SourceScanner.Enumerate(Path.Combine(dir.Path, "card"), recursive: true));
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "enumeration did not finish");
        Assert.Single(task.Result.Files);

        Directory.Delete(loop); // removes the link only
    }
}
