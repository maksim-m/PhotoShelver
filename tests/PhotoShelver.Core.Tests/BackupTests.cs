using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Importing;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// The second copy: where it goes in both layouts, and that every safety rule holds with it — when moving, the
/// source disappears only after both copies are verified (and committed); a failed second copy never loses anything.
/// </summary>
public class BackupTests
{
    private static readonly DateTime Shot = new(2026, 8, 14, 7, 12, 5);
    private static readonly DateTime Started = new(2026, 11, 10, 14, 32, 5);
    private static readonly FolderTemplate Folders = FolderTemplate.Parse("{YYYY}/{YYYY}-{MM}-{DD}");
    private static readonly TimeSpan[] Fast = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)];

    private static SourceFile File(string path) => new()
    {
        Path = path,
        Size = new FileInfo(path).Length,
        LastWriteTimeUtc = System.IO.File.GetLastWriteTimeUtc(path),
        Kind = MediaKind.Photo,
        Date = new CaptureDate(Shot, DateSource.Metadata),
    };

    /// <summary>Card, library and backup folders of one test.</summary>
    private sealed class Setup : IDisposable
    {
        public readonly TempDir Dir = new();
        public string Card => Path.Combine(Dir.Path, "card");
        public string Library => Dir.Sub("lib");
        public string BackupRoot => Dir.Sub("backup");
        public string Session => Path.Combine(BackupRoot, "2026-11-10 14-32-05");
        public string LibraryDay => Path.Combine(Library, "2026", "2026-08-14");

        public BackupPlan PerImport => new(BackupRoot, BackupLayout.PerImport, Card, ImportPlan.NewSessionFolder(BackupRoot, Started));
        public BackupPlan Mirror => new(BackupRoot, BackupLayout.Mirror, Card);

        public string CardFile(string relative, string content) => Dir.File(Path.Combine("card", relative), content);

        public List<ImportJob> Plan(BackupPlan backup, string names = "{NAME}", params string[] cardFiles) =>
            ImportPlan.CreateJobs(cardFiles.Select(File), Library, Folders, FileNameTemplate.Parse(names), backup);

        public void Dispose() => Dir.Dispose();
    }

    private static string Read(string path) => System.IO.File.ReadAllText(path);

    private static string[] AllFiles(string root) =>
        Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    // ------------------------------------------------------------------ planning

    [Fact]
    public void PerImportKeepsTheCardFoldersAndOriginalNames()
    {
        using var s = new Setup();
        var a = s.CardFile(@"DCIM\100CANON\IMG_2041.JPG", "a");
        var b = s.CardFile(@"IMG_ROOT.JPG", "b");

        var jobs = s.Plan(s.PerImport, "{YYYY}-{MM}-{DD}_{NAME}", a, b);

        Assert.Equal(Path.Combine(s.Session, "DCIM", "100CANON"), jobs[0].BackupFolder);
        Assert.Equal("IMG_2041.JPG", jobs[0].BackupFileName);      // original name, even when the library renames
        Assert.Equal("2026-08-14_IMG_2041.JPG", jobs[0].FileName);
        Assert.False(jobs[0].BackupReusesIdentical);
        Assert.Equal(s.Session, jobs[1].BackupFolder);               // a file at the top of the card
        Assert.Equal("IMG_ROOT.JPG", jobs[1].BackupFileName);
    }

    [Fact]
    public void MirrorUsesTheLibraryFoldersAndNames()
    {
        using var s = new Setup();
        var a = s.CardFile(@"DCIM\100CANON\IMG_2041.JPG", "a");

        var job = Assert.Single(s.Plan(s.Mirror, "{YYYY}-{MM}-{DD}_{NAME}", a));

        Assert.Equal(Path.Combine(s.BackupRoot, "2026", "2026-08-14"), job.BackupFolder);
        Assert.Equal("2026-08-14_IMG_2041.JPG", job.BackupFileName);
        Assert.True(job.BackupReusesIdentical);
    }

    [Fact]
    public void WithoutABackupPlanJobsHaveNoSecondCopy()
    {
        using var s = new Setup();
        var job = Assert.Single(ImportPlan.CreateJobs([File(s.CardFile("IMG.JPG", "a"))], s.Library, Folders));
        Assert.Null(job.BackupFolder);
        Assert.Equal(job.Size, job.BytesToWrite);
    }

    [Fact]
    public void SessionFolderIsNamedByTheStartTimeAndNeverReused()
    {
        using var s = new Setup();
        Assert.Equal(s.Session, ImportPlan.NewSessionFolder(s.BackupRoot, Started));

        Directory.CreateDirectory(s.Session);
        Assert.Equal(s.Session + " (2)", ImportPlan.NewSessionFolder(s.BackupRoot, Started));

        System.IO.File.WriteAllText(s.Session + " (2)", "a file with that name");
        Assert.Equal(s.Session + " (3)", ImportPlan.NewSessionFolder(s.BackupRoot, Started));
    }

    [Fact]
    public void SessionFolderOnADriveRoot()
    {
        Assert.Equal(@"X:\2026-11-10 14-32-05", ImportPlan.NewSessionFolder(@"X:\", Started));
    }

    [Fact]
    public void AFileOutsideTheSourceFolderIsRefused()
    {
        using var s = new Setup();
        var outside = s.Dir.File(@"elsewhere\IMG.JPG", "x");
        Assert.Throws<InvalidOperationException>(() => s.Plan(s.PerImport, "{NAME}", outside));
    }

    [Fact]
    public void ASessionFolderOutsideTheBackupRootIsRefused()
    {
        using var s = new Setup();
        var a = s.CardFile("IMG.JPG", "x");
        var plan = s.PerImport with { SessionFolder = Path.Combine(s.Dir.Path, "elsewhere") };
        Assert.Throws<InvalidOperationException>(() => s.Plan(plan, "{NAME}", a));
    }

    // ------------------------------------------------------------------ both copies

    public static TheoryData<ImportMode, bool, BackupLayout> AllModes()
    {
        var data = new TheoryData<ImportMode, bool, BackupLayout>();
        foreach (var layout in new[] { BackupLayout.PerImport, BackupLayout.Mirror })
        {
            data.Add(ImportMode.Copy, false, layout);
            data.Add(ImportMode.Move, false, layout);  // same volume: the library copy is an atomic rename
            data.Add(ImportMode.Move, true, layout);   // other volume: copy, verify, delete
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModes))]
    public void BothCopiesAreMadeAndVerified(ImportMode mode, bool otherVolume, BackupLayout layout)
    {
        using var s = new Setup();
        var src = s.CardFile(@"DCIM\IMG_2041.JPG", "photo");
        var jobs = s.Plan(layout == BackupLayout.Mirror ? s.Mirror : s.PerImport, "{NAME}", src);

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(jobs, mode, null, default);

        Assert.Empty(result.Failures);
        var imported = Assert.Single(result.Imported);
        Assert.Equal(Path.Combine(s.LibraryDay, "IMG_2041.JPG"), imported.DestinationPath);
        var expectedBackup = layout == BackupLayout.Mirror
            ? Path.Combine(s.BackupRoot, "2026", "2026-08-14", "IMG_2041.JPG")
            : Path.Combine(s.Session, "DCIM", "IMG_2041.JPG");
        Assert.Equal(expectedBackup, imported.BackupPath);
        Assert.Equal("photo", Read(imported.DestinationPath));
        Assert.Equal("photo", Read(expectedBackup));
        Assert.Equal(mode == ImportMode.Copy, System.IO.File.Exists(src));
        Assert.Single(AllFiles(s.BackupRoot));                         // no temp file left anywhere
        Assert.Single(AllFiles(s.Library));
    }

    [Theory]
    [InlineData(BackupLayout.PerImport)]
    [InlineData(BackupLayout.Mirror)]
    public void TheSecondCopyNeverOverwritesEither(BackupLayout layout)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "new shot");
        var plan = layout == BackupLayout.Mirror ? s.Mirror : s.PerImport;
        var folder = Assert.Single(s.Plan(plan, "{NAME}", src)).BackupFolder!;
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(Path.Combine(folder, "IMG.JPG"), "something else");

        var result = new Importer { TreatAsDifferentVolumes = true }.Run(s.Plan(plan, "{NAME}", src), ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(folder, "IMG_1.JPG"), result.Imported.Single().BackupPath);
        Assert.Equal("something else", Read(Path.Combine(folder, "IMG.JPG")));
        Assert.Equal("new shot", Read(Path.Combine(folder, "IMG_1.JPG")));
        Assert.False(System.IO.File.Exists(src));
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, false)]
    [InlineData(ImportMode.Move, true)]
    public void MirrorReusesAnIdenticalFileInsteadOfCopyingAgain(ImportMode mode, bool otherVolume)
    {
        // E.g. a file deleted from the library is imported again: the mirror already has it (under any name).
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var mirrorDay = Path.Combine(s.BackupRoot, "2026", "2026-08-14");
        Directory.CreateDirectory(mirrorDay);
        var existing = Path.Combine(mirrorDay, "renamed earlier.JPG");
        System.IO.File.WriteAllText(existing, "photo");
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(existing, stamp);

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(s.Plan(s.Mirror, "{NAME}", src), mode, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(existing, result.Imported.Single().BackupPath);
        Assert.Single(AllFiles(s.BackupRoot));                         // not copied a second time
        Assert.Equal(stamp, System.IO.File.GetLastWriteTimeUtc(existing)); // and not touched
        Assert.Equal(mode == ImportMode.Copy, System.IO.File.Exists(src));
    }

    [Fact]
    public void MirrorDoesNotReuseAFileThatOnlyHasTheSameSize()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var mirrorDay = Path.Combine(s.BackupRoot, "2026", "2026-08-14");
        Directory.CreateDirectory(mirrorDay);
        System.IO.File.WriteAllText(Path.Combine(mirrorDay, "other.JPG"), "PHOTO"); // same size, other content

        var result = new Importer { TreatAsDifferentVolumes = true }.Run(s.Plan(s.Mirror, "{NAME}", src), ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(mirrorDay, "IMG.JPG"), result.Imported.Single().BackupPath);
        Assert.Equal("photo", Read(Path.Combine(mirrorDay, "IMG.JPG")));
        Assert.Equal("PHOTO", Read(Path.Combine(mirrorDay, "other.JPG")));
    }

    [Fact]
    public void PerImportNeverReusesAnExistingFile()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var plan = s.PerImport;
        Directory.CreateDirectory(plan.SessionFolder!);
        System.IO.File.WriteAllText(Path.Combine(plan.SessionFolder!, "same.JPG"), "photo");

        var result = new Importer().Run(s.Plan(plan, "{NAME}", src), ImportMode.Copy, null, default);

        Assert.Equal(Path.Combine(plan.SessionFolder!, "IMG.JPG"), result.Imported.Single().BackupPath);
    }

    // ------------------------------------------------------------------ a failed second copy loses nothing

    [Theory]
    [MemberData(nameof(AllModes))]
    public void AFailedSecondCopyKeepsTheSourceAndStillImports(ImportMode mode, bool otherVolume, BackupLayout layout)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var jobs = s.Plan(layout == BackupLayout.Mirror ? s.Mirror : s.PerImport, "{NAME}", src);
        // The second copy's folder cannot be created: a file has that name (like a full or removed backup disk).
        Directory.CreateDirectory(Path.GetDirectoryName(jobs[0].BackupFolder!)!);
        System.IO.File.WriteAllText(jobs[0].BackupFolder!, "blocker");

        var result = new Importer { TreatAsDifferentVolumes = otherVolume }.Run(jobs, mode, null, default);

        var imported = Assert.Single(result.Imported);
        Assert.Null(imported.BackupPath);
        Assert.Equal("photo", Read(imported.DestinationPath));
        var failure = Assert.Single(result.Failures);
        Assert.Contains("вторая копия не создана", failure.Message);
        if (mode == ImportMode.Move) Assert.Contains("не удалён", failure.Message);
        Assert.Equal("photo", Read(src));                              // the source stays, also for a same-volume move
        Assert.Equal("blocker", Read(jobs[0].BackupFolder!));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void ACorruptedSecondCopyIsNotKept(ImportMode mode)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var jobs = s.Plan(s.PerImport, "{NAME}", src);
        var written = 0;
        var importer = new Importer
        {
            TreatAsDifferentVolumes = true,
            // The second copy is written first: only it gets "bit rot".
            AfterTempWritten = temp => { if (written++ == 0) System.IO.File.AppendAllText(temp, "bit rot"); },
        };

        var result = importer.Run(jobs, mode, null, default);

        Assert.Contains("ошибка записи", Assert.Single(result.Failures).Message);
        Assert.Null(Assert.Single(result.Imported).BackupPath);
        Assert.Empty(AllFiles(s.BackupRoot));                          // the corrupted temp file is removed
        Assert.Equal("photo", Read(src));
    }

    [Theory]
    [InlineData(ImportMode.Copy)]
    [InlineData(ImportMode.Move)]
    public void AFailedLibraryCopyKeepsTheSourceAndTellsWhereTheSecondCopyIs(ImportMode mode)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var jobs = s.Plan(s.PerImport, "{NAME}", src);
        Directory.CreateDirectory(Path.GetDirectoryName(jobs[0].DestinationFolder)!);
        System.IO.File.WriteAllText(jobs[0].DestinationFolder, "blocker"); // the library folder cannot be created

        var result = new Importer { TreatAsDifferentVolumes = true }.Run(jobs, mode, null, default);

        Assert.Empty(result.Imported);
        var backup = Path.Combine(s.Session, "IMG.JPG");
        Assert.Contains(backup, Assert.Single(result.Failures).Message);
        Assert.Equal("photo", Read(backup));
        Assert.Equal("photo", Read(src));
    }

    [Theory]
    [InlineData(ImportMode.Copy, false)]
    [InlineData(ImportMode.Move, true)]
    [InlineData(ImportMode.Move, false)] // same drive: the library "copy" would be a rename that does not read the file
    public void ASourceThatChangesBetweenTheCopiesIsKept(ImportMode mode, bool otherVolume)
    {
        // The source is rewritten (same size, old modification time) after the second copy and before the library copy:
        // each copy matches the source at its own time, but they differ — the source must stay.
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var stamp = System.IO.File.GetLastWriteTimeUtc(src);
        var jobs = s.Plan(s.PerImport, "{NAME}", src);
        var placed = 0;
        var importer = new Importer
        {
            TreatAsDifferentVolumes = otherVolume,
            BeforePlace = _ =>
            {
                if (placed++ != 0) return;
                System.IO.File.WriteAllText(src, "PHOTO");
                System.IO.File.SetLastWriteTimeUtc(src, stamp);
            },
        };

        var result = importer.Run(jobs, mode, null, default);

        Assert.Contains("изменился между двумя копиями", Assert.Single(result.Failures).Message);
        Assert.True(System.IO.File.Exists(src));
        Assert.Equal("photo", Read(Path.Combine(s.Session, "IMG.JPG")));
        Assert.Equal("PHOTO", Read(result.Imported.Single().DestinationPath));
    }

    // ------------------------------------------------------------------ commit before the source goes away

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCopiesAreCommittedBeforeTheSourceGoesAway(bool otherVolume)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var jobs = s.Plan(s.PerImport, "{NAME}", src);
        var commits = new List<(string Path, bool SourceStillThere)>();
        var importer = new Importer
        {
            TreatAsDifferentVolumes = otherVolume,
            CommitPlaced = path => commits.Add((path, System.IO.File.Exists(src))),
        };

        var result = importer.Run(jobs, ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        var backup = Path.Combine(s.Session, "IMG.JPG");
        Assert.Contains((backup, true), commits);
        if (otherVolume) Assert.Contains((result.Imported.Single().DestinationPath, true), commits);
        Assert.False(System.IO.File.Exists(src));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IfTheSecondCopyCannotBeCommittedTheSourceStays(bool otherVolume)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var jobs = s.Plan(s.PerImport, "{NAME}", src);
        var backup = Path.Combine(s.Session, "IMG.JPG");
        var importer = new Importer
        {
            TreatAsDifferentVolumes = otherVolume,
            RetryDelays = Fast,
            CommitPlaced = path => { if (path == backup) throw new IOException("The device is not ready."); },
        };

        var result = importer.Run(jobs, ImportMode.Move, null, default);

        Assert.Single(result.Failures);
        Assert.Equal("photo", Read(src));
        Assert.Equal("photo", Read(backup));
    }

    [Fact]
    public void AReusedMirrorFileIsNotCommittedAgain()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var mirrorDay = Path.Combine(s.BackupRoot, "2026", "2026-08-14");
        Directory.CreateDirectory(mirrorDay);
        var existing = Path.Combine(mirrorDay, "IMG.JPG");
        System.IO.File.WriteAllText(existing, "photo");
        var commits = new List<string>();

        var result = new Importer { TreatAsDifferentVolumes = true, CommitPlaced = commits.Add }
            .Run(s.Plan(s.Mirror, "{NAME}", src), ImportMode.Move, null, default);

        Assert.Empty(result.Failures);
        Assert.DoesNotContain(existing, commits);
        Assert.False(System.IO.File.Exists(src));
    }

    // ------------------------------------------------------------------ the other source protections still apply

    [Fact]
    public void ACameraProtectedSourceIsCopiedTwiceAndKept()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        System.IO.File.SetAttributes(src, FileAttributes.ReadOnly);
        try
        {
            var result = new Importer { TreatAsDifferentVolumes = true }.Run(s.Plan(s.PerImport, "{NAME}", src), ImportMode.Move, null, default);

            Assert.Contains("защищён", Assert.Single(result.Failures).Message);
            Assert.NotNull(result.Imported.Single().BackupPath);
            Assert.True(System.IO.File.Exists(src));
        }
        finally
        {
            System.IO.File.SetAttributes(src, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("..\\x.JPG")]
    [InlineData("NUL.JPG")]
    [InlineData("")]
    public void AnInvalidSecondCopyNameImportsNothing(string badName)
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var job = s.Plan(s.PerImport, "{NAME}", src)[0] with { BackupName = badName };

        var result = new Importer { TreatAsDifferentVolumes = true }.Run([job], ImportMode.Move, null, default);

        Assert.Empty(result.Imported);
        Assert.Single(result.Failures);
        Assert.Equal("photo", Read(src));
        Assert.Empty(AllFiles(s.BackupRoot));
        Assert.Empty(AllFiles(s.Library));
    }

    [Fact]
    public void ASecondCopyInTheLibraryFolderItselfIsRefused()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", "photo");
        var job = s.Plan(s.PerImport, "{NAME}", src)[0];
        job = job with { BackupFolder = job.DestinationFolder };

        var result = new Importer().Run([job], ImportMode.Move, null, default);

        Assert.Empty(result.Imported);
        Assert.Single(result.Failures);
        Assert.Equal("photo", Read(src));
    }

    // ------------------------------------------------------------------ progress and log

    [Fact]
    public void ProgressCountsBothCopies()
    {
        using var s = new Setup();
        var src = s.CardFile("IMG.JPG", new string('x', 1000));
        var reports = new List<ImportProgress>();

        new Importer { TreatAsDifferentVolumes = true }.Run(s.Plan(s.PerImport, "{NAME}", src), ImportMode.Copy,
            new SyncProgress(reports.Add), default);

        Assert.All(reports, r => Assert.Equal(2000, r.BytesTotal));
        Assert.Equal(2000, reports[^1].BytesDone);
        Assert.All(reports, r => Assert.InRange(r.BytesDone, 0, 2000));
    }

    private sealed class SyncProgress(Action<ImportProgress> report) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value) => report(value);
    }

    [Theory]
    [InlineData(ImportMode.Move)]
    [InlineData(ImportMode.Copy)]
    public void LogShowsTheSecondCopy(ImportMode mode)
    {
        using var s = new Setup();
        var ok = s.CardFile("OK.JPG", "ok");
        var bad = s.CardFile("BAD.JPG", "bad");
        var plan = s.PerImport;
        var jobs = s.Plan(plan, "{NAME}", ok, bad);
        Directory.CreateDirectory(plan.SessionFolder!);
        System.IO.File.WriteAllText(Path.Combine(plan.SessionFolder!, "BAD.JPG.blocker"), "");
        jobs[1] = jobs[1] with { BackupFolder = Path.Combine(plan.SessionFolder!, "BAD.JPG.blocker") }; // a file: cannot be a folder

        var log = ImportLog.Start(Path.Combine(s.Dir.Path, "logs"), mode, s.Card, s.Library, Folders.Pattern, jobs, "1", backup: plan);
        var result = new Importer { TreatAsDifferentVolumes = true }.Run(jobs, mode, null, default, log.Record);
        log.Finish(result);
        var lines = System.IO.File.ReadAllLines(log.Path);

        Assert.Contains($"Вторая копия: {plan.SessionFolder} (папка на каждый импорт)", lines);
        var okLine = Assert.Single(lines, l => l.StartsWith("[OK]", StringComparison.Ordinal));
        Assert.EndsWith($" + вторая копия {Path.Combine(plan.SessionFolder!, "OK.JPG")}", okLine);
        var label = mode == ImportMode.Move ? "[ОСТАВЛЕН]" : "[ЧАСТИЧНО]";
        var badLine = Assert.Single(lines, l => l.StartsWith(label, StringComparison.Ordinal));
        Assert.Contains("вторая копия не создана", badLine);
        Assert.Contains(mode == ImportMode.Move ? "из них исходник оставлен: 1" : "из них с замечанием: 1", string.Join("\n", lines));
    }

    [Fact]
    public void LogNamesTheMirrorLayout()
    {
        using var s = new Setup();
        var log = ImportLog.Start(Path.Combine(s.Dir.Path, "logs"), ImportMode.Copy, s.Card, s.Library, Folders.Pattern, [], "1", backup: s.Mirror);
        Assert.Contains($"Вторая копия: {s.BackupRoot} (те же папки и имена, что в библиотеке)", System.IO.File.ReadAllLines(log.Path));
    }

    // ------------------------------------------------------------------ end to end

    [Theory]
    [InlineData(BackupLayout.PerImport)]
    [InlineData(BackupLayout.Mirror)]
    public async Task EndToEndMoveWithASecondCopyLosesNothing(BackupLayout layout)
    {
        using var s = new Setup();
        Fixtures.CopyTo(s.Card);
        var plan = layout == BackupLayout.Mirror ? s.Mirror : s.PerImport;
        var before = new[] { Snapshot.Take(s.Card), Snapshot.Take(s.Library) };

        var outcome = await Pipeline.RunAsync(s.Card, s.Library, ImportMode.Move, names: "{YYYY}{MM}{DD}_{NAME}", backup: plan,
            importer: new Importer { TreatAsDifferentVolumes = true });

        Assert.NotEmpty(outcome.Result.Imported);
        Assert.Empty(outcome.Result.Failures);
        Invariants.NothingLost(before, [Snapshot.Take(s.Card), Snapshot.Take(s.Library)]);
        Invariants.NoTempFiles(s.Library);
        Invariants.NoTempFiles(s.BackupRoot);
        foreach (var imported in outcome.Result.Imported)
        {
            Assert.NotNull(imported.BackupPath);
            Assert.Equal(FileHasher.Hash(imported.DestinationPath), FileHasher.Hash(imported.BackupPath!));
            if (layout == BackupLayout.PerImport)
                Assert.Equal(Path.GetRelativePath(s.Card, imported.SourcePath), Path.GetRelativePath(plan.SessionFolder!, imported.BackupPath!));
            else
                Assert.Equal(Path.GetRelativePath(s.Library, imported.DestinationPath), Path.GetRelativePath(s.BackupRoot, imported.BackupPath!));
        }
        Assert.Equal(outcome.Result.Imported.Count, AllFiles(s.BackupRoot).Length);
    }

    [Fact]
    public async Task MirrorAfterTheSameCardIsImportedIntoANewLibraryHasNoDuplicates()
    {
        using var s = new Setup();
        Fixtures.CopyTo(s.Card);
        await Pipeline.RunAsync(s.Card, s.Library, ImportMode.Copy, backup: s.Mirror);
        var mirrorBefore = Snapshot.Take(s.BackupRoot);

        // A second library, the same mirror: every file is already there.
        var outcome = await Pipeline.RunAsync(s.Card, s.Dir.Sub("lib2"), ImportMode.Copy, backup: s.Mirror);

        Assert.NotEmpty(outcome.Result.Imported);
        Assert.Equal(mirrorBefore.Paths, Snapshot.Take(s.BackupRoot).Paths);
    }
}
