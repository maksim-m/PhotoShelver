using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

[assembly: CollectionBehavior(DisableTestParallelization = true)] // one app window at a time

namespace PhotoShelver.UiTests;

/// <summary>
/// UI tests open real windows, so they only run when PHOTOSHELVER_UI_TESTS=1 (tools/run_ui_tests.ps1 sets it).
/// </summary>
public sealed class UiFact : FactAttribute
{
    public UiFact()
    {
        if (Environment.GetEnvironmentVariable("PHOTOSHELVER_UI_TESTS") != "1")
            Skip = "UI tests are opt-in: run tools/run_ui_tests.ps1 (or set PHOTOSHELVER_UI_TESTS=1)";
    }
}

/// <summary>UI test on a read-only volume with the fixtures in "&lt;root&gt;\card" (tools/run_tests_sandbox.ps1 sets one up).</summary>
public sealed class ReadOnlyCardUiFact : FactAttribute
{
    public ReadOnlyCardUiFact()
    {
        if (Environment.GetEnvironmentVariable("PHOTOSHELVER_UI_TESTS") != "1")
            Skip = "UI tests are opt-in (PHOTOSHELVER_UI_TESTS=1)";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PHOTOSHELVER_TEST_READONLY_VOLUME")))
            Skip = "needs a read-only volume: run tools/run_tests_sandbox.ps1";
    }
}

/// <summary>Settings written for the app under test (same shape as the app's settings.json).</summary>
public sealed record UiSettings(
    string SourcePath,
    string TargetPath,
    string Template = "{YYYY}/{YYYY}-{MM}-{DD}",
    bool MoveMode = true,
    bool IncludeSubfolders = true,
    bool ShowFullTree = true,
    string NameTemplate = "{NAME}",
    bool BackupEnabled = false,
    string? BackupPath = null,
    bool BackupMirror = false);

public sealed record ImportDialog(string Headline, string Details, string? Warnings, IReadOnlyList<string> Failures, bool HasLogButton);

/// <summary>
/// Runs the real PhotoShelver.exe with its own settings file (never the user's) and drives it through UI Automation.
/// </summary>
public sealed class AppSession : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly Process _process;

    /// <param name="rawSettings">The settings file's exact content instead of <paramref name="settings"/> (e.g. a damaged file).</param>
    public AppSession(UiSettings settings, string workDir, IReadOnlyDictionary<string, string>? environment = null, string? rawSettings = null)
    {
        // SAFETY: the real app is started here, and it moves and deletes files. Every folder it is given must be a
        // test folder (or one of the test volumes), so that no mistake in a test can point it at real photos.
        RequireTestPath(workDir, "work folder");
        SettingsPath = Path.Combine(workDir, "settings.json");
        RequireTestPath(SettingsPath, "settings file");
        foreach (var (name, path) in new[] { ("source", settings.SourcePath), ("target", settings.TargetPath), ("second copy", settings.BackupPath) })
            if (!string.IsNullOrEmpty(path)) RequireTestPath(path, name);
        if (rawSettings is not null)
            foreach (var path in PathsIn(rawSettings)) RequireTestPath(path, "path in the settings file");
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
            if (key.EndsWith("_LOG", StringComparison.Ordinal) || key.EndsWith("_SETTINGS", StringComparison.Ordinal)) RequireTestPath(value, key);

        File.WriteAllText(SettingsPath, rawSettings ?? JsonSerializer.Serialize(new
        {
            settings.SourcePath,
            settings.TargetPath,
            settings.Template,
            settings.IncludeSubfolders,
            settings.MoveMode,
            settings.ShowFullTree,
            settings.NameTemplate,
            settings.BackupEnabled,
            settings.BackupPath,
            settings.BackupMirror,
            WindowWidth = 1280,
            WindowHeight = 900,
            WindowMaximized = false,
        }));

        var start = new ProcessStartInfo(ExePath) { UseShellExecute = false };
        start.Environment["PHOTOSHELVER_LANG"] = "ru"; // the tests check Russian texts unless they pass another language
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        start.Environment["PHOTOSHELVER_SETTINGS"] = SettingsPath; // SAFETY: the user's settings are never used
        _process = Process.Start(start) ?? throw new InvalidOperationException("could not start the app");

        try
        {
            var deadline = DateTime.UtcNow + DefaultTimeout;
            while (_process.MainWindowHandle == IntPtr.Zero)
            {
                if (DateTime.UtcNow > deadline || _process.HasExited) throw new InvalidOperationException("the app window did not appear");
                Thread.Sleep(100);
                _process.Refresh();
            }
            Window = AutomationElement.FromHandle(_process.MainWindowHandle);
            // A standard Windows message box (class #32770) instead of the main window: the app failed to start and
            // waits for someone to press OK. Fail now with its text instead of waiting on it.
            if (Window.Current.ClassName == "#32770")
            {
                var text = string.Join(" ", Window.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                    .Cast<AutomationElement>().Select(e => e.Current.Name));
                throw new InvalidOperationException($"the app showed a message instead of its window: {text}");
            }
        }
        catch
        {
            // The session never reaches its Dispose: never leave the app running behind the tests.
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Refuses (throws before the app starts) any path outside %TEMP%\PhotoShelverTests, except the test volumes the
    /// volume test script sets up (PHOTOSHELVER_TEST_VOLUME, and PHOTOSHELVER_TEST_READONLY_VOLUME only while it really
    /// is write-protected).
    /// </summary>
    internal static void RequireTestPath(string path, string what)
    {
        if (PhotoShelver.Core.Tests.TempDir.IsTestPath(path)) return;
        var writable = Environment.GetEnvironmentVariable("PHOTOSHELVER_TEST_VOLUME");
        if (!string.IsNullOrEmpty(writable) && IsOn(path, writable)) return;
        var readOnly = Environment.GetEnvironmentVariable("PHOTOSHELVER_TEST_READONLY_VOLUME");
        if (!string.IsNullOrEmpty(readOnly) && IsOn(path, readOnly) && PhotoShelver.Core.PathUtil.IsReadOnlyVolume(readOnly)) return;
        throw new InvalidOperationException($"SAFETY: the {what} “{path}” is not a test folder — refusing to start the app");
    }

    private static bool IsOn(string path, string volumeRoot) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(volumeRoot), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every string value of a property named "...Path" in a settings file (a damaged file has none).</summary>
    private static IEnumerable<string> PathsIn(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            return document.RootElement.EnumerateObject()
                .Where(p => p.Name.EndsWith("Path", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String)
                .Select(p => p.Value.GetString()!)
                .Where(p => p.Length > 0)
                .ToList();
        }
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
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoShelver.sln"))) dir = dir.Parent;
            if (dir is null) throw new InvalidOperationException("repository root not found");
            var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
            var exe = Path.Combine(dir.FullName, "src", "PhotoShelver.App", "bin", configuration, "net10.0-windows", "PhotoShelver.exe");
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

    // ---------------- real mouse input (for double clicks and context menus) ----------------

    static AppSession() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: UIA and cursor coordinates agree

    /// <summary>Clicks with the real mouse at a point of the element (fractions of its width/height).</summary>
    public void MouseClick(AutomationElement element, bool right = false, bool twice = false, double fx = 0.5, double fy = 0.5)
    {
        var r = element.Current.BoundingRectangle;
        var (x, y) = ((int)(r.Left + r.Width * fx), (int)(r.Top + r.Height * fy));
        // A real click goes to whatever window is on top at that point. Windows does not let a background process take
        // the foreground while someone uses the computer, so the app may open behind another window: bring it to the
        // front, and never click anything that is not the app.
        var covering = "";
        WaitUntil(() =>
        {
            BringToFront();
            var top = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /* GA_ROOT */);
            if (top == _process.MainWindowHandle) return true;
            covering = DescribeWindow(top);
            return false;
        }, "the app window to be on top at the click point", TimeSpan.FromSeconds(10), () => $"covered by {covering}");
        SetCursorPos(x, y);
        Thread.Sleep(100);
        var (down, up) = right ? (0x0008u, 0x0010u) : (0x0002u, 0x0004u);
        for (var i = 0; i < (twice ? 2 : 1); i++)
        {
            mouse_event(down, 0, 0, 0, IntPtr.Zero);
            mouse_event(up, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(60);
        }
    }

    /// <summary>Right-clicks the element and invokes an item of the context menu that opens.</summary>
    public void ContextMenu(AutomationElement element, string itemAutomationId)
    {
        MouseClick(element, right: true);
        AutomationElement? item = null;
        WaitUntil(() => (item = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, itemAutomationId)))) is not null, $"context menu item '{itemAutomationId}'");
        Click(item!);
    }

    /// <summary>
    /// Foreground for the app's window. A short Alt press releases Windows' foreground lock (the documented way an
    /// app that received input may hand the foreground over); then SetForegroundWindow is allowed.
    /// </summary>
    private void BringToFront()
    {
        var hwnd = _process.MainWindowHandle;
        if (GetForegroundWindow() == hwnd) return;
        keybd_event(0x12 /* VK_MENU */, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 0x2 /* KEYEVENTF_KEYUP */, IntPtr.Zero);
        ShowWindow(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
        Thread.Sleep(150);
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        var title = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { process = "?"; }
        return $"window “{title}” of process {process}";
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    public void SetTemplate(string text) =>
        ((ValuePattern)Find("Template").GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);

    /// <summary>Opens the "Advanced" section (its content exists only when expanded) and types a file name template.</summary>
    public void SetNameTemplate(string text)
    {
        var advanced = Find("Advanced");
        ((ExpandCollapsePattern)advanced.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        WaitUntil(() => TryFind("NameTemplate") is not null, "file name template field");
        ((ValuePattern)Find("NameTemplate").GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
    }

    /// <summary>Header of the "Advanced" section (shows a non-default setting even when collapsed).</summary>
    public string AdvancedHeader => string.Join(" ", Find("Advanced")
        .FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
        .Cast<AutomationElement>().Select(e => e.Current.Name).Take(1));

    public void ExpandAdvanced()
    {
        ((ExpandCollapsePattern)Find("Advanced").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();
        WaitUntil(() => TryFind("NameTemplate") is not null, "the Advanced section to open");
    }

    public void CollapseAdvanced() =>
        ((ExpandCollapsePattern)Find("Advanced").GetCurrentPattern(ExpandCollapsePattern.Pattern)).Collapse();

    /// <summary>Waits until scanning and the duplicate check are over and the summary is shown.</summary>
    public void WaitReady()
    {
        WaitUntil(() =>
        {
            var texts = Window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))
                .Cast<AutomationElement>().Select(e => e.Current.Name).ToList();
            // Russian and English texts (a test may run the app in either language).
            string[] busy = ["Чтение метаданных", "Проверка дубликатов", "Поиск файлов", "Reading metadata", "Checking for duplicates", "Looking for files"];
            return texts.Any(t => t.StartsWith("Всего", StringComparison.Ordinal) || t.StartsWith("Total", StringComparison.Ordinal))
                   && !texts.Any(t => busy.Any(b => t.StartsWith(b, StringComparison.Ordinal)));
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

    public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout, Func<string> detail)
    {
        try
        {
            WaitUntil(condition, what, timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} ({detail()})", ex);
        }
    }

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
        var dir = Path.Combine(Path.GetTempPath(), "PhotoShelverUiTests");
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
