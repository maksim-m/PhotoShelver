using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>Where files go, reading from disk, volumes, long paths and recovery after an interrupted import.</summary>
public class PlacementAndRecoveryTests
{
    private static ImportJob Job(string src, string destFolder) => new(src, destFolder, new FileInfo(src).Length);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(1 << 20)]
    [InlineData((1 << 20) + 1)]
    [InlineData(3 * (1 << 20) + 123)]
    [InlineData(FileHasher.BufferSize - 1)] // around the chunk boundary of unbuffered reads
    [InlineData(FileHasher.BufferSize)]
    [InlineData(FileHasher.BufferSize + 1)]
    [InlineData(2 * FileHasher.BufferSize + 4096)]
    public void HashFromDiskMatchesNormalHash(int size)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "f.bin");
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        File.WriteAllBytes(path, bytes);

        // NTFS supports unbuffered reads, so the cache must really be bypassed here.
        Assert.Equal(new DiskHash(FileHasher.Hash(path), BypassedCache: true), FileHasher.HashFromDisk(path));
    }

    [Fact]
    public void HashFromDiskOfMissingFileThrows()
    {
        using var dir = new TempDir();
        Assert.Throws<FileNotFoundException>(() => FileHasher.HashFromDisk(Path.Combine(dir.Path, "missing.bin")));
    }

    [Fact]
    public void WritabilityCheckDoesNotOpenTheFileAndRespectsReadOnlyAttribute()
    {
        using var dir = new TempDir();
        var normal = dir.File("a.jpg", "a");
        var readOnly = dir.File("b.jpg", "b");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        Assert.True(PathUtil.IsWritable(normal));
        Assert.False(PathUtil.IsWritable(readOnly));
        Assert.False(PathUtil.IsReadOnlyVolume(normal));

        // Holding the file exclusively must not change the answer: the check never opens it.
        using (new FileStream(normal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(PathUtil.IsWritable(normal));
        File.SetAttributes(readOnly, FileAttributes.Normal);
    }

    [Fact]
    public void SameVolumeDetection()
    {
        using var dir = new TempDir();
        var a = dir.Sub("a");
        Assert.True(PathUtil.IsSameVolume(a, Path.Combine(dir.Path, @"not\yet\created")));

        var otherDrive = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed && !a.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase));
        if (otherDrive is not null)
            Assert.False(PathUtil.IsSameVolume(a, otherDrive.RootDirectory.FullName));
    }

    [Fact]
    public void JobsUseTemplateFolderUnderTargetRoot()
    {
        var root = @"C:\Library";
        var file = new SourceFile
        {
            Path = @"E:\DCIM\IMG_1.JPG", Size = 1, LastWriteTimeUtc = DateTime.UtcNow, Kind = MediaKind.Photo,
            Date = new CaptureDate(new DateTime(2026, 3, 7, 23, 59, 0), DateSource.Metadata),
        };
        var broken = new SourceFile { Path = @"E:\DCIM\BAD.JPG", Size = 1, LastWriteTimeUtc = DateTime.UtcNow, Kind = MediaKind.Photo, Error = "x" };

        var jobs = ImportPlan.CreateJobs([file, broken], root, FolderTemplate.Parse("{YYYY}/{YYYY}-{MM}-{DD}"));

        var job = Assert.Single(jobs);
        Assert.Equal(@"C:\Library\2026\2026-03-07", job.DestinationFolder);
        Assert.Equal(file.Path, job.SourcePath);
    }

    /// <summary>Property: no template that passes validation can place files outside the target root.</summary>
    [Fact]
    public void NoValidTemplateEscapesTheTargetRoot()
    {
        var rnd = new Random(2026);
        string[] pieces = ["{YYYY}", "{YY}", "{MM}", "{MMM}", "{DD}", "{", "}", "{NAME}", "YYYY", "/", "\\", ".", "..", " ", "-", "a", ":", "C:", "~", "%"];
        var root = @"C:\Library";
        var file = new SourceFile
        {
            Path = @"E:\x.jpg", Size = 1, LastWriteTimeUtc = DateTime.UtcNow, Kind = MediaKind.Photo,
            Date = new CaptureDate(new DateTime(2026, 12, 31), DateSource.Metadata),
        };
        var valid = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var pattern = string.Concat(Enumerable.Range(0, rnd.Next(1, 8)).Select(_ => pieces[rnd.Next(pieces.Length)]));
            if (!FolderTemplate.TryParse(pattern, out var template, out _)) continue;
            valid++;
            var folder = ImportPlan.DestinationFolder(root, template!, file); // throws if it escaped
            Assert.StartsWith(root + "\\", folder, StringComparison.OrdinalIgnoreCase);
        }
        Assert.True(valid > 500, $"only {valid} valid templates generated");
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)]
    public void LongDestinationPaths(ImportMode mode, bool crossVolume)
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "long");
        var dest = Path.Combine(dir.Path, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
        Assert.True(dest.Length > 300);

        var result = new Importer { TreatAsDifferentVolumes = crossVolume }.Run([Job(src, dest)], mode, null, default);

        // Long paths are enabled on Windows 11 by policy; if a machine has them disabled the file must stay safe.
        if (result.Failures.Count == 0)
        {
            Assert.Equal("long", File.ReadAllText(Path.Combine(dest, "IMG.JPG")));
            Assert.Equal(mode == ImportMode.Copy, File.Exists(src));
        }
        else
        {
            Assert.True(File.Exists(src));
        }
    }

    [Fact]
    public async Task InterruptedImportLeftoverIsNotMistakenForAnImportedFile()
    {
        // A crash after the copy was written but before it was renamed leaves "<name>.<guid>.importing".
        using var dir = new TempDir();
        var source = Path.Combine(dir.Path, "card");
        var target = Path.Combine(dir.Path, "library");
        Fixtures.CopyTo(source);
        var leftoverFolder = Path.Combine(target, @"2026\11-05");
        Directory.CreateDirectory(leftoverFolder);
        var leftover = Path.Combine(leftoverFolder, "IMG_0002.JPG.0123456789abcdef.importing");
        File.Copy(Path.Combine(source, @"photos\IMG_0002.JPG"), leftover);

        var outcome = await Pipeline.RunAsync(source, target, ImportMode.Copy);

        var img2 = outcome.Files.Single(f => f.FileName == "IMG_0002.JPG");
        Assert.Equal(DuplicateState.New, outcome.Duplicates[img2].State);
        Assert.True(File.Exists(Path.Combine(leftoverFolder, "IMG_0002.JPG")));
        Assert.True(File.Exists(leftover)); // the program never deletes files it did not create in this run
    }
}
