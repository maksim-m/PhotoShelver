using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // one app window at a time

namespace PhotoImporter.UiTests;

/// <summary>
/// UI tests open real windows, so they only run when PHOTOIMPORTER_UI_TESTS=1 (tools/run_ui_tests.ps1 sets it).
/// </summary>
public sealed class UiFact : FactAttribute
{
    public UiFact()
    {
        if (Environment.GetEnvironmentVariable("PHOTOIMPORTER_UI_TESTS") != "1")
            Skip = "UI tests are opt-in: run tools/run_ui_tests.ps1 (or set PHOTOIMPORTER_UI_TESTS=1)";
    }
}

/// <summary>UI test on a read-only volume with the fixtures in "&lt;root&gt;\card" (tools/run_real_volume_tests.ps1).</summary>
public sealed class ReadOnlyCardUiFact : FactAttribute
{
    public ReadOnlyCardUiFact()
    {
        if (Environment.GetEnvironmentVariable("PHOTOIMPORTER_UI_TESTS") != "1")
            Skip = "UI tests are opt-in (PHOTOIMPORTER_UI_TESTS=1)";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHOTOIMPORTER_TEST_READONLY_VOLUME")))
            Skip = "needs a read-only volume: run tools/run_real_volume_tests.ps1";
    }
}

/// <summary>Settings written for the app under test (same shape as the app's settings.json).</summary>
public sealed record UiSettings(
    string SourcePath,
    string TargetPath,
    string Template = "YYYY/YYYY-MM-DD",
    bool MoveMode = true,
    bool IncludeSubfolders = true,
    bool ShowFullTree = true);

public sealed record ImportDialog(string Headline, string Details, string? Warnings, IReadOnlyList<string> Failures, bool HasLogButton);

/// <summary>
/// Runs the real PhotoImporter.exe with its own settings file (never the user's) and drives it through UI Automation.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly Process _process;

    public AppSession(UiSettings settings, string workDir)
    {
        SettingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new
        {
            settings.SourcePath,
            settings.TargetPath,
            settings.Template,
            settings.IncludeSubfolders,
            settings.MoveMode,
            settings.ShowFullTree,
            WindowWidth = 1280,
            WindowHeight = 900,
            WindowMaximized = false,
        }));

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        start.Environment["PHOTOIMPORTER_SETTINGS"] = SettingsPath; // SAFETY: the user's settings are never used
        _process = Process.Start(start) ?? throw new InvalidOperationException("could not start the app");

        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (_process.MainWindowHandle == IntPtr.Zero)
        {
            if (DateTime.UtcNow > deadline || _process.HasExited) throw new InvalidOperationException("the app window did not appear");
            Thread.Sleep(100);
            _process.Refresh();
        }
        Window = AutomationElement.FromHandle(_process.MainWindowHandle);
    }

    public string SettingsPath { get; }

    public AutomationElement Window { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>The app's exe from the same configuration as these tests (Debug/Release).</summary>
    public static string ExePath
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoImporter.sln"))) dir = dir.Parent;
            if (dir is null) throw new InvalidOperationException("repository root not found");
            var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            var exe = Path.Combine(dir.FullName, "src", "PhotoImporter.App", "bin", configuration, "net10.0-windows", "PhotoImporter.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("build the app first", exe);
            return exe;
        }
    }

    // ---------------- finding elements ----------------

    public AutomationElement? TryFind(string automationId, AutomationElement? root = null) =>
        (root ?? Window).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public AutomationElement Find(string automationId, AutomationElement? root = null)
    {
        AutomationElement? found = null;
        WaitUntil(() => (found = TryFind(automationId, root)) is not null, $"element '{automationId}'");
        return found!;
    }

    public List<AutomationElement> FindAll(string automationId, AutomationElement? root = null) =>
        (root ?? Window).FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))
            .Cast<AutomationElement>().ToList();

    /// <summary>Text of a TextBlock (null when it is not shown).</summary>
    public string? Text(string automationId) => TryFind(automationId)?.Current.Name;

    public string Summary => Text("Summary") ?? "";

    public string? Notice => Text("Notice");

    public bool ImportEnabled => Find("Import").Current.IsEnabled;

    /// <summary>Names of all realized tree nodes, e.g. "2026-11-05 +2 NEW".</summary>
    public List<string> TreeNodes() =>
        Find("TargetTree").FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();

    // ---------------- actions ----------------

    public void Click(string automationId) => Click(Find(automationId));

    public static void Click(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static ToggleState State(AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    public static void Toggle(AutomationElement element) =>
        ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Toggle();

    public static void Select(AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public static bool IsSelected(AutomationElement element) =>
        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    public void SetTemplate(string text) =>
        ((ValuePattern)Find("Template").GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>Waits until scanning and the duplicate check are over and the summary is shown.</summary>
    public void WaitReady()
    {
        WaitUntil(() =>
        {
            var texts = Window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();
            return texts.Any(t => t.StartsWith("Всего", StringComparison.Ordinal))
                   && !texts.Any(t => t.StartsWith("Чтение метаданных", StringComparison.Ordinal)
                                      || t.StartsWith("Проверка дубликатов", StringComparison.Ordinal)
                                      || t.StartsWith("Поиск файлов", StringComparison.Ordinal));
        }, "scan and duplicate check to finish");
        Thread.Sleep(300); // let the debounced refresh (counters, tree) settle
    }

    /// <summary>Clicks Import, waits for the summary dialog, reads it and closes it.</summary>
    public ImportDialog RunImport()
    {
        StartImport();
        return WaitForImportDialog();
    }

    public void StartImport()
    {
        Assert.True(ImportEnabled, "Import button is disabled. " + Diagnostics());
        Click("Import");
    }

    /// <summary>Asks the window to close (like the X button) without waiting.</summary>
    public void RequestClose() => _process.CloseMainWindow();

    /// <summary>Waits for the import summary dialog, reads it and closes it.</summary>
    public ImportDialog WaitForImportDialog()
    {
        AutomationElement? dialog = null;
        WaitUntil(() => (dialog = Window.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))) is not null
                        && TryFind("SummaryHeadline", dialog) is not null, "import summary dialog", TimeSpan.FromSeconds(120));
        var failures = TryFind("SummaryFailures", dialog)?
            .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
            .Cast<AutomationElement>().Select(e => e.Current.Name).ToList() ?? [];
        var result = new ImportDialog(
            Find("SummaryHeadline", dialog).Current.Name,
            Find("SummaryDetails", dialog).Current.Name,
            TryFind("SummaryWarnings", dialog)?.Current.Name,
            failures,
            TryFind("SummaryOpenLog", dialog) is not null);
        Click(Find("SummaryClose", dialog));
        WaitUntil(() => Window.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)) is null, "summary dialog to close");
        return result;
    }

    /// <summary>Answers a standard Windows message box (Yes = 6, No = 7, language-independent).</summary>
    public void AnswerMessageBox(bool yes)
    {
        AutomationElement? box = null;
        WaitUntil(() => (box = Window.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "#32770"))) is not null
                        || (box = AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                            new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                            new PropertyCondition(AutomationElement.ClassNameProperty, "#32770")))) is not null, "message box");
        Click(Find(yes ? "6" : "7", box));
    }

    /// <summary>Closes the window like the user does (settings are saved).</summary>
    public void CloseGracefully()
    {
        _process.CloseMainWindow();
        if (!_process.WaitForExit(15_000)) throw new InvalidOperationException("the app did not close");
    }

    // ---------------- waiting & diagnostics ----------------

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            try
            {
                if (condition()) return;
            }
            catch (ElementNotAvailableException)
            {
                // UI changed while we were looking: try again.
            }
            if (_process.HasExited) throw new InvalidOperationException($"the app exited (code {_process.ExitCode}) while waiting for {what}");
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}. {Diagnostics()}");
            Thread.Sleep(150);
        }
    }

    /// <summary>State of the window for failure messages, plus a screenshot.</summary>
    public string Diagnostics()
    {
        string shot;
        try { shot = Screenshot(); }
        catch (Exception ex) { shot = "screenshot failed: " + ex.Message; }
        string Safe(Func<string?> f) { try { return f() ?? "-"; } catch (Exception ex) { return ex.GetType().Name; } }
        return $"Summary: «{Safe(() => Summary)}», notice: «{Safe(() => Notice)}», import enabled: {Safe(() => ImportEnabled.ToString())}, screenshot: {shot}";
    }

    public string Screenshot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoImporterUiTests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ui-{DateTime.Now:HHmmss-fff}.png");
        var hwnd = _process.MainWindowHandle;
        GetWindowRect(hwnd, out var r);
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, r.Right - r.Left, r.Bottom - r.Top);
        var old = SelectObject(mem, bitmap);
        PrintWindow(hwnd, mem, 2);
        SelectObject(mem, old);
        var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(bitmap);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
