using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// What is checked before an import starts (and what the app shows while folders are chosen): folders that are
/// missing, write-protected or inside each other, the second copy, a write-protected card for a move, free space.
/// </summary>
public class ImportPreflightTests
{
    private static readonly DateTime Shot = new(2026, 8, 14, 7, 12, 5);
    private static readonly DateTime Started = new(2026, 11, 10, 14, 32, 5);
    private static readonly FolderTemplate Template = FolderTemplate.Parse("{YYYY}/{YYYY}-{MM}-{DD}");

    /// <summary>Card, library and second-copy folders of one test (all on one test volume).</summary>
    private sealed class Setup : IDisposable
    {
        public readonly TempDir Dir = new();
        public string Card => Dir.Sub("card");
        public string Library => Dir.Sub("lib");
        public string BackupRoot => Dir.Sub("backup");

        public SourceFile Photo(string name, string content = "photo")
        {
            var path = Dir.File(Path.Combine("card", name), content);
            return new SourceFile
            {
                Path = path,
                Size = new FileInfo(path).Length,
                LastWriteTimeUtc = File.GetLastWriteTimeUtc(path),
                Kind = MediaKind.Photo,
                Date = new CaptureDate(Shot, DateSource.Metadata),
            };
        }

        public ImportRequest Request(IReadOnlyList<SourceFile> files, ImportMode mode = ImportMode.Copy,
            bool backup = false, string? backupPath = null, bool mirror = false, string? target = null) =>
            new(files, Card, target ?? Library, mode, Template, FileNameTemplate.Parse(FileNameTemplate.Default),
                backup, backupPath ?? (backup ? BackupRoot : null), mirror, Started);

        public void Dispose() => Dir.Dispose();
    }

    /// <summary>Everything in place: the base for the cases below, each changing one thing.</summary>
    private static FolderState Fine => new()
    {
        Source = @"E:\DCIM", Target = @"D:\Photos", BackupEnabled = true, Backup = @"F:\Backup",
        SourceExists = true, TargetExists = true, BackupExists = true,
    };

    // ------------------------------------------------------------------ judging a reading

    [Fact]
    public void FoldersInPlaceBlockNothing()
    {
        Assert.Null(Fine.BlockingReason);
        Assert.Null(Fine.BackupWarning);
        Assert.True(Fine.IsReady);
    }

    public static TheoryData<string, FolderState, string> Blocked => new()
    {
        { "source missing", Fine with { SourceExists = false }, Strings.SourceFolderNotFound },
        { "target missing", Fine with { TargetExists = false }, Strings.TargetFolderNotFound },
        { "target write-protected", Fine with { TargetReadOnly = true }, Strings.TheTargetFolderIsOnAWrite },
        { "source and target nested", Fine with { SourceTargetNested = true }, Strings.TheSourceAndTheTargetMustNot },
        { "second copy without a folder", Fine with { Backup = " " }, Strings.ChooseAFolderForTheSecondCopy },
        { "second copy folder missing", Fine with { BackupExists = false }, Strings.BackupFolderNotFound },
        { "second copy write-protected", Fine with { BackupReadOnly = true }, Strings.BackupFolderWriteProtected },
        { "second copy inside the source", Fine with { BackupNestedWithSource = true }, Strings.BackupInsideSource },
        { "second copy inside the library", Fine with { BackupNestedWithTarget = true }, Strings.BackupInsideTarget },
    };

    [Theory]
    [MemberData(nameof(Blocked))]
    public void EachProblemBlocksTheImport(string name, FolderState state, string reason)
    {
        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(reason, state.BlockingReason);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void NoFolderChosenYetIsNoProblemToShowButNoImportEither()
    {
        var empty = new FolderState();
        Assert.Null(empty.BlockingReason);
        Assert.False(empty.IsReady);
    }

    [Fact]
    public void ProblemsOfAnUnusedSecondCopyDoNotMatter()
    {
        var off = Fine with { BackupEnabled = false, BackupExists = false, BackupReadOnly = true, BackupOnTargetVolume = true };
        Assert.Null(off.BlockingReason);
        Assert.Null(off.BackupWarning);
        Assert.True(off.IsReady);
    }

    [Fact]
    public void AMissingFolderIsReportedBeforeAnythingElse()
    {
        var all = Fine with { SourceExists = false, TargetReadOnly = true, SourceTargetNested = true, BackupExists = false };
        Assert.Equal(Strings.SourceFolderNotFound, all.BlockingReason);
    }

    [Fact]
    public void ASecondCopyOnTheSameDriveWorksButIsWarnedAbout()
    {
        Assert.Equal(Strings.BackupOnLibraryDrive, (Fine with { BackupOnTargetVolume = true, BackupOnSourceVolume = true }).BackupWarning);
        Assert.Equal(Strings.BackupOnSourceDrive, (Fine with { BackupOnSourceVolume = true }).BackupWarning);
        Assert.Null((Fine with { BackupOnTargetVolume = true }).BlockingReason);
        // A second copy that cannot be made gets its problem, not a warning.
        Assert.Null((Fine with { BackupOnTargetVolume = true, BackupExists = false }).BackupWarning);
    }

    [Fact]
    public void AReadingIsOnlyForTheFoldersItWasMadeFor()
    {
        Assert.True(Fine.IsFor(@"E:\DCIM", @"D:\Photos", true, @"F:\Backup"));
        Assert.False(Fine.IsFor(@"E:\DCIM", @"D:\Other", true, @"F:\Backup"));
        Assert.False(Fine.IsFor(@"E:\DCIM", @"D:\Photos", false, @"F:\Backup"));
        Assert.False(Fine.IsFor(@"E:\DCIM", @"D:\Photos", true, null));
    }

    // ------------------------------------------------------------------ reading the folders

    [Fact]
    public void ReadsWhatIsOnTheDisk()
    {
        using var s = new Setup();
        var state = new ImportPreflight().ReadFolders(s.Card, s.Library, true, s.BackupRoot);
        Assert.True(state.SourceExists && state.TargetExists && state.BackupExists);
        Assert.False(state.SourceTargetNested || state.BackupNestedWithSource || state.BackupNestedWithTarget);
        Assert.True(state.BackupOnTargetVolume); // all in %TEMP%
        Assert.Equal(Strings.BackupOnLibraryDrive, state.BackupWarning);
        Assert.True(state.IsFor(s.Card, s.Library, true, s.BackupRoot));
    }

    [Fact]
    public void ReadsMissingAndNestedFolders()
    {
        using var s = new Setup();
        var preflight = new ImportPreflight();
        Assert.Equal(Strings.TargetFolderNotFound, preflight.ReadFolders(s.Card, Path.Combine(s.Dir.Path, "gone"), false, null).BlockingReason);
        Assert.Equal(Strings.TheSourceAndTheTargetMustNot, preflight.ReadFolders(s.Card, s.Dir.Sub("card", "lib"), false, null).BlockingReason);
        Assert.Equal(Strings.BackupInsideTarget, preflight.ReadFolders(s.Card, s.Library, true, s.Dir.Sub("lib", "copy")).BlockingReason);
        Assert.Equal(Strings.BackupInsideSource, preflight.ReadFolders(s.Card, s.Library, true, s.Card).BlockingReason);
    }

    [Fact]
    public void AWriteProtectedVolumeIsRead()
    {
        using var s = new Setup();
        var preflight = new ImportPreflight { IsReadOnlyVolume = p => p == s.Library };
        Assert.Equal(Strings.TheTargetFolderIsOnAWrite, preflight.ReadFolders(s.Card, s.Library, false, null).BlockingReason);
        preflight = new ImportPreflight { IsReadOnlyVolume = p => p == s.BackupRoot };
        Assert.Equal(Strings.BackupFolderWriteProtected, preflight.ReadFolders(s.Card, s.Library, true, s.BackupRoot).BlockingReason);
    }

    [Fact]
    public void ADiskThatFailsToAnswerNeverCountsAsFine()
    {
        using var s = new Setup();
        var preflight = new ImportPreflight { IsReadOnlyVolume = _ => throw new IOException("The device is not ready.") };
        var state = preflight.ReadFolders(s.Card, s.Library, false, null); // never throws
        Assert.True(state.TargetExists);
        // Not knowing whether it is write-protected: the import itself finds out per file (and keeps the sources).
        Assert.False(state.TargetReadOnly);
        // Paths that cannot be compared count as nested: blocked.
        var weird = new ImportPreflight().ReadFolders("E:\\a\0b", s.Library, false, null);
        Assert.True(weird.SourceTargetNested);
        Assert.NotNull(weird.BlockingReason);
    }

    // ------------------------------------------------------------------ the check at the click

    [Fact]
    public void AReadyImportGetsItsJobs()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg"), s.Photo("b.jpg") };
        var check = new ImportPreflight().Check(s.Request(files));
        Assert.Null(check.Problem);
        Assert.Equal(2, check.Jobs!.Count);
        Assert.All(check.Jobs, j => Assert.Equal(Path.Combine(s.Library, "2026", "2026-08-14"), j.DestinationFolder));
        Assert.Null(check.Backup);
        Assert.True(check.Folders.IsReady);
    }

    [Fact]
    public void FilesThatCannotBeImportedGiveNoJobs()
    {
        using var s = new Setup();
        var broken = s.Photo("x.jpg");
        broken.Error = "unreadable";
        var check = new ImportPreflight().Check(s.Request([broken]));
        Assert.Null(check.Problem);
        Assert.Empty(check.Jobs!);
    }

    [Fact]
    public void TheFoldersAreReadAgainAtTheClick()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg") };
        var request = s.Request(files, backup: true);
        Directory.Delete(s.BackupRoot); // e.g. the backup disk was unplugged a second ago
        var check = new ImportPreflight().Check(request);
        Assert.Equal(Strings.BackupFolderNotFound, check.Problem);
        Assert.Null(check.Jobs);
        Assert.False(check.Folders.BackupExists);
    }

    [Fact]
    public void ASecondCopyThatWasAskedForIsAlwaysPlanned()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg") };

        var perImport = new ImportPreflight().Check(s.Request(files, backup: true));
        Assert.Null(perImport.Problem);
        Assert.Equal(BackupLayout.PerImport, perImport.Backup!.Layout);
        Assert.Equal(Path.Combine(s.BackupRoot, "2026-11-10 14-32-05"), perImport.Jobs![0].BackupFolder);

        var mirror = new ImportPreflight().Check(s.Request(files, backup: true, mirror: true));
        Assert.Equal(BackupLayout.Mirror, mirror.Backup!.Layout);
        Assert.Equal(Path.Combine(s.BackupRoot, "2026", "2026-08-14"), mirror.Jobs![0].BackupFolder);
        Assert.True(mirror.Jobs[0].BackupReusesIdentical);
    }

    [Fact]
    public void ASecondCopyWithoutAFolderStopsTheImport()
    {
        using var s = new Setup();
        var check = new ImportPreflight().Check(s.Request([s.Photo("a.jpg")], backup: true, backupPath: ""));
        Assert.Equal(Strings.ChooseAFolderForTheSecondCopy, check.Problem);
    }

    [Fact]
    public void AMoveFromAWriteProtectedCardOffersToCopy()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg") };
        var preflight = new ImportPreflight { IsReadOnlyVolume = p => p.StartsWith(s.Card, StringComparison.OrdinalIgnoreCase) };

        var move = preflight.Check(s.Request(files, ImportMode.Move));
        Assert.Equal(Strings.TheSourceDriveIsWriteProtectedE, move.Problem);
        Assert.True(move.OfferCopy);

        Assert.Null(preflight.Check(s.Request(files, ImportMode.Copy)).Problem);
    }

    [Fact]
    public void NotEnoughSpaceInTheLibraryStopsTheImport()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg", new string('x', 1000)), s.Photo("b.jpg", new string('x', 500)) };
        var preflight = new ImportPreflight { FreeSpace = _ => 1499 };
        var check = preflight.Check(s.Request(files));
        Assert.Equal(L.F(Strings.NotEnoughSpaceOnTheTargetDrive, L.Bytes(1500), L.Bytes(1499)), check.Problem);
        Assert.Null(check.Jobs);

        Assert.Null(new ImportPreflight { FreeSpace = _ => 1500 }.Check(s.Request(files)).Problem);
        Assert.Null(new ImportPreflight { FreeSpace = _ => null }.Check(s.Request(files)).Problem); // unknown: not blocked
    }

    [Fact]
    public void AMoveWithinOneDriveNeedsNoSpace()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg", new string('x', 1000)) };
        var sameDrive = new ImportPreflight { FreeSpace = _ => 0, IsSameVolume = (_, _) => true };
        Assert.Null(sameDrive.Check(s.Request(files, ImportMode.Move)).Problem);
        Assert.NotNull(sameDrive.Check(s.Request(files, ImportMode.Copy)).Problem);
        var otherDrive = new ImportPreflight { FreeSpace = _ => 0, IsSameVolume = (_, _) => false };
        Assert.NotNull(otherDrive.Check(s.Request(files, ImportMode.Move)).Problem);
    }

    [Fact]
    public void TheSecondCopyNeedsItsOwnSpace()
    {
        using var s = new Setup();
        var files = new[] { s.Photo("a.jpg", new string('x', 1000)) };

        // On its own drive: checked there.
        var backupFull = new ImportPreflight { IsSameVolume = (_, _) => false, FreeSpace = p => p == s.BackupRoot ? 999 : 10_000 };
        Assert.Equal(L.F(Strings.NotEnoughSpaceForTheSecondCopy, L.Bytes(1000), L.Bytes(999)),
            backupFull.Check(s.Request(files, backup: true)).Problem);

        // On the library's drive: both copies must fit there.
        var sharedDrive = new ImportPreflight { IsSameVolume = (_, _) => true, FreeSpace = _ => 1999 };
        Assert.Equal(L.F(Strings.NotEnoughSpaceOnTheTargetDrive, L.Bytes(2000), L.Bytes(1999)),
            sharedDrive.Check(s.Request(files, backup: true)).Problem);
        Assert.Null(new ImportPreflight { IsSameVolume = (_, _) => true, FreeSpace = _ => 2000 }.Check(s.Request(files, backup: true)).Problem);
    }
}
