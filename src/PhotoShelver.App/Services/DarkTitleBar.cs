using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoShelver.App.Services;

/// <summary>
/// The dark title bar on Windows before 11 (Windows 10, Server 2022). There WPF's Fluent theme makes the window's
/// content dark but leaves the title bar white; on Windows 11 WPF does it itself, so nothing is changed there.
/// </summary>
internal static class DarkTitleBar
{
    private const int Windows11Build = 22000;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;          // Windows 10 20H1 and later
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19; // Windows 10 1809 - 1909

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Register()
    {
        if (Environment.OSVersion.Version.Build >= Windows11Build) return;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Apply((Window)s)));
        // The Windows theme switched while the app runs.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (Window window in Application.Current.Windows) Apply(window);
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

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark() ? 1 : 0;
        // Only cosmetic: a Windows that knows neither attribute simply keeps its title bar.
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref dark, sizeof(int));
    }
}
