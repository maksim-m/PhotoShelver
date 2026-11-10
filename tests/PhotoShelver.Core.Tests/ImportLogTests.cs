using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Importing;

namespace PhotoShelver.Core.Tests;

public class ImportLogTests
{
    private static ImportJob Job(string src, string destFolder) => new(src, destFolder, new FileInfo(src).Length);

    private static (ImportResult Result, ImportLog Log) RunWithLog(TempDir dir, IReadOnlyList<ImportJob> jobs, ImportMode mode, Importer? importer = null)
    {
        var log = ImportLog.Start(Path.Combine(dir.Path, "logs"), mode, @"E:\DCIM", Path.Combine(dir.Path, "dst"), "{YYYY}/{YYYY}-{MM}-{DD}", jobs, "1.2.3",
            new DateTime(2026, 11, 9, 14, 30, 12));
        var result = (importer ?? new Importer()).Run(jobs, mode, null, default, log.Record);
        log.Finish(result, new DateTime(2026, 11, 9, 14, 31, 0));
        return (result, log);
    }

    [Fact]
    public void LogListsEveryFileWithItsOutcome()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "dst");
        var ok = dir.File(@"src\OK.JPG", "fine");
        var missing = Path.Combine(dir.Path, @"src\MISSING.JPG");
        var protectedFile = dir.File(@"src\PROTECTED.JPG", "camera-protected");
        File.SetAttributes(protectedFile, FileAttributes.ReadOnly);

        var (result, log) = RunWithLog(dir,
            [Job(ok, dest), new ImportJob(missing, dest, 10), Job(protectedFile, dest)],
            ImportMode.Move, new Importer { TreatAsDifferentVolumes = true });
        File.SetAttributes(protectedFile, FileAttributes.Normal);

        Assert.Null(log.Error);
        Assert.Equal(Path.Combine(dir.Path, "logs", "import-2026-11-09_14-30-12.log"), log.Path);
        var lines = File.ReadAllLines(log.Path);

        Assert.Equal("PhotoShelver 1.2.3 — журнал импорта", lines[0]);
        Assert.Contains("Режим:    перемещение", lines);
        Assert.Contains("Файлов:   3 (" , string.Join("\n", lines));

        var okLine = Assert.Single(lines, l => l.StartsWith("[OK]", StringComparison.Ordinal));
        Assert.Contains($"{ok} → {Path.Combine(dest, "OK.JPG")}", okLine);
        Assert.Contains($"xxh128 {FileHasher.Hash(Path.Combine(dest, "OK.JPG")):x32}", okLine); // the hash of the file on disk

        Assert.Contains(lines, l => l.StartsWith("[ОШИБКА]", StringComparison.Ordinal) && l.Contains(missing));
        var keptLine = Assert.Single(lines, l => l.StartsWith("[ОСТАВЛЕН]", StringComparison.Ordinal));
        Assert.Contains(protectedFile, keptLine);
        Assert.Contains("защищён", keptLine);

        Assert.Contains("Итог:     импортировано 2 (из них исходник оставлен: 1), ошибок 1", lines);
        Assert.Equal(2, result.Imported.Count);
    }

    [Fact]
    public void SameVolumeMoveIsLoggedWithoutHash()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "x");
        var (_, log) = RunWithLog(dir, [Job(src, Path.Combine(dir.Path, "dst"))], ImportMode.Move);

        Assert.Contains(File.ReadAllLines(log.Path), l => l.StartsWith("[OK]", StringComparison.Ordinal) && l.Contains("переименованием"));
    }

    [Fact]
    public void CancelledImportIsMarkedInTheSummary()
    {
        using var dir = new TempDir();
        var a = dir.File(@"src\a.jpg", "a");
        var b = dir.File(@"src\b.jpg", "b");
        using var cts = new CancellationTokenSource();
        var log = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, "s", "t", "YYYY", [Job(a, "x"), Job(b, "x")], "1");
        var result = new Importer().Run([Job(a, Path.Combine(dir.Path, "dst")), Job(b, Path.Combine(dir.Path, "dst"))],
            ImportMode.Copy, null, cts.Token, outcome => { log.Record(outcome); cts.Cancel(); });
        log.Finish(result);

        var text = File.ReadAllText(log.Path);
        Assert.Contains("импорт отменён", text);
        Assert.Single(File.ReadAllLines(log.Path), l => l.StartsWith("[OK]", StringComparison.Ordinal));
    }

    [Fact]
    public void LogIsReadableWhenTheImportNeverFinished()
    {
        // Every line is written as soon as a file is done: a crash leaves a usable log without the summary.
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "x");
        var job = Job(src, Path.Combine(dir.Path, "dst"));
        var log = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, "s", "t", "YYYY", [job], "1");
        new Importer().Run([job], ImportMode.Copy, null, default, log.Record);
        // (no Finish: the app "crashed" here)

        var lines = File.ReadAllLines(log.Path);
        Assert.Contains(lines, l => l.StartsWith("[OK]", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("Итог", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoImportsInTheSameSecondGetSeparateLogs()
    {
        using var dir = new TempDir();
        var time = new DateTime(2026, 1, 1, 10, 0, 0);
        var first = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, "s", "t", "YYYY", [], "1", time);
        var second = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, "s", "t", "YYYY", [], "1", time);

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path) && File.Exists(second.Path));
    }

    [Fact]
    public void UnwritableLogDoesNotStopTheImport()
    {
        using var dir = new TempDir();
        var blocker = dir.File("logs", "a file where the log folder should be");
        var src = dir.File(@"src\IMG.JPG", "x");
        var job = Job(src, Path.Combine(dir.Path, "dst"));

        var log = ImportLog.Start(blocker, ImportMode.Copy, "s", "t", "YYYY", [job], "1");
        var result = new Importer().Run([job], ImportMode.Copy, null, default, log.Record);
        log.Finish(result);

        Assert.NotNull(log.Error);
        Assert.Single(result.Imported);
        Assert.Equal("x", File.ReadAllText(Path.Combine(dir.Path, @"dst\IMG.JPG")));
    }

    [Fact]
    public void ImportedFilesCarryTheVerifiedHash()
    {
        using var dir = new TempDir();
        var src = dir.File(@"src\IMG.JPG", "content");
        var result = new Importer().Run([Job(src, Path.Combine(dir.Path, "dst"))], ImportMode.Copy, null, default);

        var imported = Assert.Single(result.Imported);
        Assert.Equal(FileHasher.Hash(src), imported.Hash);
    }
}
