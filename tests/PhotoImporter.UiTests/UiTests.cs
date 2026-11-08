using System.Text.Json;
using System.Windows.Automation;
using PhotoImporter.Core;
using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Importing;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Scanning;
using PhotoImporter.Core.Tests;

namespace PhotoImporter.UiTests;

/// <summary>
/// End-to-end scenarios through the real window. Each test works on a fresh copy of the fixtures
/// in its own temp folder ("card" = source, "lib" = target) with its own settings file.
/// </summary>
[Trait("Category", "UI")]
public class UiTests
{
    private const string Template = "YYYY/YYYY-MM-DD";

    private sealed class Workspace : IDisposable
    {
        private readonly TempDir _dir = new();

        public Workspace()
        {
            Card = Path.Combine(_dir.Path, "card");
            Library = Path.Combine(_dir.Path, "lib");
            Fixtures.CopyTo(Card);
            Directory.CreateDirectory(Library);
        }

        public string Root => _dir.Path;
        public string Card { get; }
        public string Library { get; }

        public void Dispose() => _dir.Dispose();
    }

    /// <summary>Where the core puts each fixture (file name → folder relative to the library), in this computer's time zone.</summary>
    private static async Task<Dictionary<string, string>> ExpectedFolders(string card, string library, string template = Template)
    {
        var files = SourceScanner.Enumerate(card, recursive: true).Files;
        await SourceScanner.ReadMetadataAsync(files, new CaptureDateReader(), null, default);
        var tpl = FolderTemplate.Parse(template);
        return files.Where(f => f.Date is not null).ToDictionary(
            f => Path.GetRelativePath(card, f.Path),
            f => Path.GetRelativePath(library, ImportPlan.DestinationFolder(library, tpl, f)),
            StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ import

    [UiFact]
    public async Task MoveImportRespectsExistingLibrary()
    {
        using var ws = new Workspace();
        var expected = await ExpectedFolders(ws.Card, ws.Library);
        var day0001 = Path.Combine(ws.Library, expected[@"photos\IMG_0001.JPG"]);
        var day0002 = Path.Combine(ws.Library, expected[@"photos\IMG_0002.JPG"]);
        Directory.CreateDirectory(day0001);
        Directory.CreateDirectory(day0002);
        File.WriteAllText(Path.Combine(day0001, "IMG_0001.JPG"), "unrelated photo");                        // name clash
        File.Copy(Path.Combine(ws.Card, @"photos\IMG_0002.JPG"), Path.Combine(day0002, "already.jpg"));   // already imported
        var before = new[] { Snapshot.Take(ws.Card), Snapshot.Take(ws.Library) };

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();
        Assert.Contains("Уже в цели 1", app.Summary.Replace(' ', ' '));
        Assert.Contains("Дубликатов в источнике 1", app.Summary.Replace(' ', ' '));

        var dialog = app.RunImport();

        Assert.Equal("Импорт завершён", dialog.Headline);
        Assert.Contains("Импортировано: 15 файлов", dialog.Details);
        Assert.Empty(dialog.Failures);

        var after = new[] { Snapshot.Take(ws.Card), Snapshot.Take(ws.Library) };
        Invariants.NothingLost(before, after);
        Invariants.TargetFilesUnchanged(before[1], after[1]);
        Invariants.NoTempFiles(ws.Library);

        // Every imported file is in its folder; the clashing one got "_1"; the duplicate stayed on the card.
        foreach (var (file, folder) in expected)
        {
            if (file is @"photos\IMG_0002.JPG" or @"photos\sub\IMG_0001_copy.JPG") continue;
            var name = Path.GetFileName(file) == "IMG_0001.JPG" ? "IMG_0001_1.JPG" : Path.GetFileName(file);
            Assert.True(File.Exists(Path.Combine(ws.Library, folder, name)), $"{file} is not in {folder}");
        }
        Assert.Equal("unrelated photo", File.ReadAllText(Path.Combine(day0001, "IMG_0001.JPG")));
        Assert.Equal(
            [@"other\IMG_0001.XMP", @"other\notes.txt", @"photos\IMG_0002.JPG", @"photos\sub\IMG_0001_copy.JPG"],
            Snapshot.Take(ws.Card).Paths);

        // After the import the card is rescanned: nothing new is left.
        app.WaitReady();
        Assert.Contains("Новых 0", app.Summary.Replace(' ', ' '));
        Assert.False(app.ImportEnabled);
    }

    [UiFact]
    public async Task CopyImportLeavesTheCardUntouched()
    {
        using var ws = new Workspace();
        var expected = await ExpectedFolders(ws.Card, ws.Library);
        var cardBefore = Snapshot.Take(ws.Card);

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library, MoveMode: false), ws.Root);
        app.WaitReady();
        var dialog = app.RunImport();

        Assert.Contains("Импортировано: 16 файлов", dialog.Details);
        Assert.Equal(cardBefore.Files, Snapshot.Take(ws.Card).Files);
        var library = Snapshot.Take(ws.Library);
        foreach (var (file, folder) in expected.Where(e => e.Key != @"photos\sub\IMG_0001_copy.JPG"))
            Assert.Equal(cardBefore.Files[file], library.Files[Path.Combine(folder, Path.GetFileName(file))]);
    }

    // ------------------------------------------------------------------ duplicate check

    [UiFact]
    public async Task TargetAppearingLaterIsCheckedBeforeImport()
    {
        using var ws = new Workspace();
        var expected = await ExpectedFolders(ws.Card, ws.Library);
        Directory.Delete(ws.Library); // the disk is not plugged in yet

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();
        Assert.False(app.ImportEnabled);
        Assert.Contains("цель не найдена", app.Notice ?? "");

        // The disk appears and already holds IMG_0002 in its day folder.
        var day = Path.Combine(ws.Library, expected[@"photos\IMG_0002.JPG"]);
        Directory.CreateDirectory(day);
        File.Copy(Path.Combine(ws.Card, @"photos\IMG_0002.JPG"), Path.Combine(day, "already.jpg"));

        // No click: the app notices the folder, re-checks duplicates and only then enables the import.
        app.WaitUntil(() => app.ImportEnabled && app.Summary.Replace(' ', ' ').Contains("Уже в цели 1"),
            "duplicate re-check after the target appeared");
    }

    // ------------------------------------------------------------------ template

    [UiFact]
    public void TemplateErrorsBlockImportAndValidTemplateRebuildsTree()
    {
        using var ws = new Workspace();
        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();
        Assert.True(app.ImportEnabled);

        app.SetTemplate("yyyy/mm");
        app.WaitUntil(() => app.Text("TemplateError") is { } e && e.Contains("заглавными"), "template error");
        Assert.False(app.ImportEnabled);

        app.SetTemplate("MM-DD");
        app.WaitUntil(() => app.Text("TemplateWarning") is { } w && w.Contains("Нет года"), "template warning");

        app.SetTemplate("YYYY/MM");
        app.WaitUntil(() => app.ImportEnabled, "import enabled with the new template");
        Assert.Null(app.Text("TemplateError"));
        app.WaitUntil(() => app.TreeNodes().Any(n => n.StartsWith("11 +", StringComparison.Ordinal)), "month folder in the tree");
        Assert.DoesNotContain(app.TreeNodes(), n => n.StartsWith("2026-", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ selection

    [UiFact]
    public void SelectionButtonsAndDayCheckbox()
    {
        using var ws = new Workspace();
        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();
        string Summary() => app.Summary.Replace(' ', ' ');
        Assert.Contains("Выбрано 16", Summary());

        app.Click("SelectNone");
        app.WaitUntil(() => Summary().Contains("Выбрано 0"), "nothing selected");
        Assert.False(app.ImportEnabled);

        app.Click("SelectAllNew");
        app.WaitUntil(() => Summary().Contains("Выбрано 16"), "all new selected again");

        // 5 Nov: IMG_0001 + IMG_0002 are new, the copy in "sub" is a duplicate and must never be ticked in bulk.
        // Collapse first so that every day header is on screen (rows outside the view are not created).
        app.Click("CollapseAll");
        app.WaitUntil(() => app.FindAll("TileCheck").Count == 0, "days collapsed");
        var day = app.FindAll("DayCheck").Single(e => e.Current.Name == "5 ноября 2026");
        Assert.Equal(ToggleState.On, AppSession.State(day));
        AppSession.Toggle(day);
        app.WaitUntil(() => Summary().Contains("Выбрано 14"), "day unticked");
        AppSession.Toggle(day);
        app.WaitUntil(() => Summary().Contains("Выбрано 16"), "day ticked again (duplicate stays unticked)");

        // Unticking one tile makes the day partial.
        AppSession.Click(app.FindAll("DayHeader").Single(e => e.Current.Name == "5 ноября 2026"));
        AutomationElement? tile = null;
        app.WaitUntil(() => (tile = app.FindAll("TileCheck").FirstOrDefault(e => e.Current.Name == "IMG_0001.JPG")) is not null, "IMG_0001 tile");
        day = app.FindAll("DayCheck").Single(e => e.Current.Name == "5 ноября 2026");
        AppSession.Toggle(tile!);
        app.WaitUntil(() => AppSession.State(app.FindAll("DayCheck").Single(e => e.Current.Name == "5 ноября 2026")) == ToggleState.Indeterminate,
            "day checkbox partial");
        Assert.Contains("Выбрано 15", Summary());
    }

    [UiFact]
    public void CollapsingAndExpandingDaysDoesNotCrash()
    {
        using var ws = new Workspace();
        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();

        app.Click("CollapseAll");
        app.WaitUntil(() => app.FindAll("TileCheck").Count == 0, "all days collapsed");

        // Regression: re-expanding a day used to crash the app (recycled row containers).
        // Headers are looked up again before every click: expanding a day re-uses row containers on screen.
        var days = app.FindAll("DayHeader").Select(e => e.Current.Name).ToList();
        Assert.True(days.Count >= 5, $"only {days.Count} day headers");
        for (var round = 0; round < 3; round++)
        {
            foreach (var name in days)
            {
                var header = app.FindAll("DayHeader").FirstOrDefault(e => e.Current.Name == name);
                if (header is not null) AppSession.Click(header);
                Thread.Sleep(150);
            }
        }
        app.Click("ExpandAll");
        app.WaitUntil(() => app.FindAll("TileCheck").Count > 0, "days expanded");
        Assert.False(app.HasExited);
        Assert.Contains("Выбрано 16", app.Summary.Replace(' ', ' '));
    }

    // ------------------------------------------------------------------ guards & leftovers

    [UiFact]
    public void NestedSourceAndTargetBlockTheImport()
    {
        using var ws = new Workspace();
        using var app = new AppSession(new UiSettings(ws.Card, Path.Combine(ws.Card, "photos")), ws.Root);
        app.WaitUntil(() => (app.Notice ?? "").Contains("не должны совпадать"), "nesting notice");
        Assert.False(app.ImportEnabled);
    }

    [UiFact]
    public void LeftoversOfAnInterruptedImportCanBeDeleted()
    {
        using var ws = new Workspace();
        var leftover = Path.Combine(ws.Library, @"2026\2026-11-05\IMG_0001.JPG.0123abcd.importing");
        var userFile = Path.Combine(ws.Library, @"2026\2026-11-05\notes.importing");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "partial copy");
        File.WriteAllText(userFile, "not ours");

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitUntil(() => (app.Text("Leftovers") ?? "").Contains("1 файл"), "leftovers panel");

        app.Click("DeleteLeftovers");
        app.AnswerMessageBox(yes: true);
        app.WaitUntil(() => app.Text("Leftovers") is null, "leftovers panel to disappear");

        Assert.False(File.Exists(leftover));
        Assert.Equal("not ours", File.ReadAllText(userFile));
    }

    [UiFact]
    public void LeftoverDeletionCanBeCancelled()
    {
        using var ws = new Workspace();
        var leftover = Path.Combine(ws.Library, @"2026\IMG_0001.JPG.0123abcd.importing");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "partial copy");

        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitUntil(() => app.Text("Leftovers") is not null, "leftovers panel");
        app.Click("DeleteLeftovers");
        app.AnswerMessageBox(yes: false);
        Thread.Sleep(1000);

        Assert.True(File.Exists(leftover));
        Assert.NotNull(app.Text("Leftovers"));
    }

    // ------------------------------------------------------------------ settings

    [UiFact]
    public void SettingsAreSavedOnClose()
    {
        using var ws = new Workspace();
        using var app = new AppSession(new UiSettings(ws.Card, ws.Library), ws.Root);
        app.WaitReady();

        app.SetTemplate("YYYY/MM/DD");
        AppSession.Select(app.Find("ModeCopy"));
        app.WaitUntil(() => app.ImportEnabled, "recheck after the template change");
        app.CloseGracefully();

        using var json = JsonDocument.Parse(File.ReadAllText(app.SettingsPath));
        Assert.Equal("YYYY/MM/DD", json.RootElement.GetProperty("Template").GetString());
        Assert.False(json.RootElement.GetProperty("MoveMode").GetBoolean());
        Assert.Equal(ws.Card, json.RootElement.GetProperty("SourcePath").GetString());
    }
}
