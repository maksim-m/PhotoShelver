using System.Diagnostics;
using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// The same folder or file reached through another name (a junction, a hard link) and file names near the
/// 255-character limit: none of them may make the importer treat one thing as two, or fail where it need not.
/// </summary>
public class AliasAndLongNameTests
{
    /// <summary>A junction (needs no administrator rights, unlike a symbolic link).</summary>
    private static void Junction(string link, string target) => Run($"mklink /J \"{link}\" \"{target}\"");

    private static void HardLink(string link, string target) => Run($"mklink /H \"{link}\" \"{target}\"");

    private static void Run(string command)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c {command}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        using var process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void NestingIsSeenThroughAJunction()
    {
        using var dir = new TempDir();
        var library = dir.Sub("library");
        var card = dir.Sub("library", "import");
        var alias = Path.Combine(dir.Path, "alias");
        Junction(alias, card);

        Assert.True(PathUtil.AreNestedOrEqual(alias, library));          // alias → library\import
        Assert.True(PathUtil.AreNestedOrEqual(library, alias));
        Assert.True(PathUtil.AreNestedOrEqual(alias, card));
        Assert.True(PathUtil.AreNestedOrEqual(Path.Combine(alias, "sub"), library)); // a folder that does not exist yet
        Assert.False(PathUtil.AreNestedOrEqual(alias, dir.Sub("elsewhere")));
        Assert.Equal(card, PathUtil.CanonicalPath(alias), ignoreCase: true);
    }

    [Fact]
    public void CanonicalPathOfAPlainOrMissingPathIsItsFullPath()
    {
        using var dir = new TempDir();
        Assert.Equal(dir.Path, PathUtil.CanonicalPath(dir.Path), ignoreCase: true);
        var missing = Path.Combine(dir.Path, "not", "there");
        Assert.Equal(missing, PathUtil.CanonicalPath(missing), ignoreCase: true);
    }

    [Fact]
    public void SameFileIsRecognisedThroughAJunctionAndAHardLink()
    {
        using var dir = new TempDir();
        var card = dir.Sub("card");
        var file = dir.File(@"card\IMG.JPG", "photo");
        var other = dir.File(@"card\OTHER.JPG", "photo");
        var alias = Path.Combine(dir.Path, "alias");
        Junction(alias, card);
        var hard = Path.Combine(dir.Path, "hard.JPG");
        HardLink(hard, file);

        Assert.True(PathUtil.IsSameFile(file, file.ToLowerInvariant()));
        Assert.True(PathUtil.IsSameFile(file, Path.Combine(alias, "IMG.JPG")));
        Assert.True(PathUtil.IsSameFile(file, hard));
        Assert.False(PathUtil.IsSameFile(file, other));                     // same content, different file
        Assert.False(PathUtil.IsSameFile(file, Path.Combine(dir.Path, "missing.JPG")));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void MirrorNeverTakesTheSourceItselfForTheSecondCopy(ImportMode mode)
    {
        // The mirror's day folder is the card folder under another name: the source must not count as its own copy
        // (a move would then delete the only "second copy").
        using var dir = new TempDir();
        var card = dir.Sub("card");
        var src = dir.File(@"card\IMG.JPG", "photo");
        var backupRoot = dir.Sub("backup");
        Directory.CreateDirectory(Path.Combine(backupRoot, "2026"));
        Junction(Path.Combine(backupRoot, "2026", "2026-08-14"), card);
        var file = new SourceFile
        {
            Path = src, Size = 5, LastWriteTimeUtc = File.GetLastWriteTimeUtc(src), Kind = MediaKind.Photo,
            Date = new CaptureDate(new DateTime(2026, 8, 14, 7, 0, 0), DateSource.Metadata),
        };
        var jobs = ImportPlan.CreateJobs([file], dir.Sub("lib"), FolderTemplate.Parse("{YYYY}/{YYYY}-{MM}-{DD}"), null,
            new BackupPlan(backupRoot, BackupLayout.Mirror, card));

        var result = new Importer { TreatAsDifferentVolumes = true }.Run(jobs, mode, null, default);

        var imported = Assert.Single(result.Imported);
        Assert.NotNull(imported.BackupPath);
        Assert.False(PathUtil.IsSameFile(imported.BackupPath!, src));
        Assert.Equal("photo", File.ReadAllText(imported.BackupPath!));      // a real second copy exists
        Assert.Equal("photo", File.ReadAllText(imported.DestinationPath));
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, true)]
    public void ANameNearTheLimitIsImportedDespiteItsLongerTempName(ImportMode mode, bool otherVolume)
    {
        using var dir = new TempDir();
        var src = dir.File(@"card\IMG.JPG", "photo");
        var longName = new string('n', 250) + ".JPG"; // 254 characters: valid, but "<name>.xxxxxxxx.importing" is not
        var job = new ImportJob(src, dir.Sub("lib"), 5, longName);

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run([job], mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(dir.Path, "lib", longName), result.Imported.Single().DestinationPath);
        Assert.Equal("photo", File.ReadAllText(result.Imported.Single().DestinationPath));
        Invariants.NoTempFiles(dir.Path);
    }

    [Fact]
    public void TempNamesStayWithinTheLimitAndAreRecognisedAsLeftovers()
    {
        foreach (var name in new[] { "IMG.JPG", new string('a', 236), new string('a', 300), new string('b', 235) + "😀" + "c", new string('d', 230) + "...... x" })
        {
            var temp = $"{Importer.TempBase(name)}.0123abcd.importing";
            Assert.True(temp.Length <= 255, $"{temp.Length}");
            Assert.True(ImportLeftovers.IsLeftover(temp));
            Assert.False(char.IsHighSurrogate(Importer.TempBase(name)[^1]));
            Assert.False(Importer.TempBase(name).EndsWith('.') || Importer.TempBase(name).EndsWith(' '));
        }
        Assert.Equal("IMG.JPG", Importer.TempBase("IMG.JPG"));
    }
}
