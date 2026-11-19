using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoShelver.App.Services;

/// <summary>
/// The dark title bar on Windows before 11 (Windows 10, Server 2022). There WPF's Fluent theme makes the window's
/// content dark but leaves the title bar (drawn by Windows) light; on Windows 11 WPF does it itself, so nothing is
/// changed there. Set before the window is shown: set later, Windows 10 repaints only part of the title bar.
/// </summary>
internal static class DarkTitleBar
{
    private const int Windows11Build = 22000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;              // Windows 10 20H1 and later
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;  // Windows 10 1809 - 1909
    private const uint SWP_FRAMECHANGED_ONLY = 0x0020 | 0x0001 | 0x0002 | 0x0004 | 0x0010; // FRAMECHANGED, NOSIZE, NOMOVE, NOZORDER, NOACTIVATE

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private static bool Needed => Environment.OSVersion.Version.Build < Windows11Build;
    private static bool _watching;

    /// <summary>Called in a window's constructor.</summary>
    public static void Attach(Window window)
    {
        if (!Needed) return;
        window.SourceInitialized += (_, _) => Apply(window, redraw: false);
        if (_watching) return;
        _watching = true;
        // The Windows theme switched while the app runs.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (Window w in Application.Current.Windows) Apply(w, redraw: true);
            });
        };
    }

    /// <summary>The same rule as ThemeMode="System": dark when the apps' theme of Windows is dark.</summary>
    private static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void Apply(Window window, bool redraw)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark() ? 1 : 0;
        // Only cosmetic: a Windows that knows neither attribute simply keeps its title bar.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
        if (redraw) SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED_ONLY);
    }
}
