using System.Text.RegularExpressions;
using System.Windows.Automation;
using PhotoImporter.Core;
using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Tests;

namespace PhotoImporter.UiTests;

/// <summary>Scenarios that need an import long enough to interact with it, and a write-protected card.</summary>
public class LongImportAndCardUiTests
{
    private const int FileCount = 5;
    private const int FileSizeMb = 300; // ~1.5 GB in total: a few seconds of copying and verifying

    /// <summary>A "card" with a few large video files (pseudo-random content, distinct per file) and an empty library.</summary>
    private sealed class BigWorkspace : IDisposable
    {
        private readonly TempDir _dir = new();

        public BigWorkspace()
        {
            var free = new DriveInfo(Path.GetPathRoot(_dir.Path)!).AvailableFreeSpace;
            if (free < 6L * FileCount * FileSizeMb * 1024 * 1024 / 2)
                throw new InvalidOperationException($"not enough free space in %TEMP% for this test ({free / (1 << 30)} GB free)");

            Card = Path.Combine(_dir.Path, "card");
            Library = Path.Combine(_dir.Path, "lib");
            Directory.CreateDirectory(Card);
            Directory.CreateDirectory(Library);
            var buffer = new byte[1 << 20];
            for (var f = 0; f < FileCount; f++)
            {
                new Random(f).NextBytes(buffer);
                var path = Path.Combine(Card, $"MVI_{f:D4}.MP4");
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 << 20))
                {
                    for (var mb = 0; mb < FileSizeMb; mb++)
                    {
                        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), mb); // every megabyte differs
                        stream.Write(buffer);
                    }
                }
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 7, 1 + f, 12, 0, 0, DateTimeKind.Utc));
            }
        }

        public string Root => _dir.Path;
        public string Card { get; }
        public string Library { get; }

        public void Dispose() => _dir.Dispose();
    }

    private static int ImportedCount(ImportDialog dialog)
    {
        var m = Regex.Match(dialog.Details, @"Импортировано: (\d+)");
        Assert.True(m.Success, dialog.Details);
        return int.Parse(m.Groups[1].Value);
    }

    [UiFact]
    [Trait("Category", "UI")]
    public void CancelStopsAfterTheCurrentFile()
    {
        using var ws = new BigWorkspace();
        var cardBefore = Snapshot.Take(ws.Card);

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library, MoveMode: false), ws.Root);
        app.WaitReady();
        app.StartImport();
        app.WaitUntil(() => (app.Text("ImportStatus") ?? "").StartsWith("Импорт:", StringComparison.Ordinal), "import to start");
        app.Click("CancelImport");
        var dialog = app.WaitForImportDialog();

        Assert.Equal("Импорт отменён", dialog.Headline);
        Assert.Contains("Оставшиеся файлы не тронуты", dialog.Details);
        Assert.Empty(dialog.Failures);
        var imported = ImportedCount(dialog);
        Assert.InRange(imported, 1, FileCount - 1); // the current file is finished, the rest is not started

        // Whatever was imported is complete and identical; nothing half-written is left; the card is unchanged.
        var library = Snapshot.Take(ws.Library);
        Assert.Equal(imported, library.Files.Count);
        foreach (var (path, hash) in library.Files)
            Assert.Equal(cardBefore.Files[Path.GetFileName(path)], hash);
        Invariants.NoTempFiles(ws.Library);
        Assert.Equal(cardBefore.Files, Snapshot.Take(ws.Card).Files);

        // The rest can be imported afterwards.
        app.WaitReady();
        Assert.Contains($"Уже в цели {imported}", app.Summary.Replace(' ', ' '));
    }

    [UiFact]
    [Trait("Category", "UI")]
    public void WindowCannotBeClosedDuringAnImport()
    {
        using var ws = new BigWorkspace();

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library, MoveMode: false), ws.Root);
        app.WaitReady();
        app.StartImport();
        app.WaitUntil(() => (app.Text("ImportStatus") ?? "").StartsWith("Импорт:", StringComparison.Ordinal), "import to start");

        app.RequestClose();
        app.WaitUntil(() => (app.Notice ?? "").Contains("Идёт импорт"), "'import in progress' notice");
        Assert.False(app.HasExited);

        var dialog = app.WaitForImportDialog(); // the import runs to the end
        Assert.Equal("Импорт завершён", dialog.Headline);
        Assert.Equal(FileCount, ImportedCount(dialog));
        Invariants.NoTempFiles(ws.Library);

        app.CloseGracefully(); // now the window closes normally
        Assert.True(app.HasExited);
    }

    [ReadOnlyCardUiFact]
    [Trait("Category", "RealVolumeUi")]
    public void MoveFromWriteProtectedCardOffersCopyInstead()
    {
        var card = Path.Combine(Environment.GetEnvironmentVariable("PHOTOIMPORTER_TEST_READONLY_VOLUME")!, "card");
        // SAFETY: this test starts a move from <volume>\card - only on a volume that really is read-only.
        Assert.True(PathUtil.IsReadOnlyVolume(card), $"SAFETY: {card} is not on a read-only volume - refusing to run");
        var cardBefore = Snapshot.Take(card);

        using var local = new TempDir();
        var library = Path.Combine(local.Path, "lib");
        Directory.CreateDirectory(library);
        using var app = new AppSession(new UiSettings(card, library, MoveMode: true), local.Path);
        app.WaitReady();

        // Move: refused up front with an explanation and a one-click alternative; no dialog, nothing copied.
        app.StartImport();
        app.WaitUntil(() => (app.Notice ?? "").Contains("защищён от записи"), "write-protection notice");
        Assert.Empty(Directory.EnumerateFileSystemEntries(library));

        AppSession.Click(app.Find("SwitchToCopy"));
        app.WaitUntil(() => AppSession.IsSelected(app.Find("ModeCopy")), "copy mode selected");

        var dialog = app.RunImport();
        Assert.Equal("Импорт завершён", dialog.Headline);
        Assert.Equal(16, ImportedCount(dialog)); // the duplicate in "sub" is not imported
        Assert.Empty(dialog.Failures);
        Assert.Equal(cardBefore.Files, Snapshot.Take(card).Files);
        Assert.Equal(16, Snapshot.Take(library).Files.Count);
    }
}
