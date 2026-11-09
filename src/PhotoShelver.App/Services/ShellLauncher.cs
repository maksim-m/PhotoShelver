using System.ComponentModel;
using System.Diagnostics;

namespace PhotoShelver.App.Services;

/// <summary>
/// Opens files in their default program and shows them in Explorer.
/// PHOTOSHELVER_SHELL_LOG (UI tests) makes it write "open|path" / "reveal|path" lines to that file instead.
/// </summary>
public static class ShellLauncher
{
    private static readonly string? TestLog = Environment.GetEnvironmentVariable("PHOTOSHELVER_SHELL_LOG");

    /// <summary>Opens the file in the program Windows associates with it ("Open with" when there is none).</summary>
    public static void Open(string path)
    {
        if (Record("open", path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // ERROR_NO_ASSOCIATION
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}") { UseShellExecute = false });
        }
    }

    /// <summary>Opens Explorer in the file's folder with the file selected.</summary>
    public static void ShowInFolder(string path)
    {
        if (Record("reveal", path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    private static bool Record(string action, string path)
    {
        if (string.IsNullOrEmpty(TestLog)) return false;
        // Shared append, so a test reading the log at the same moment cannot make this fail.
        using var stream = new FileStream(TestLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var writer = new StreamWriter(stream);
        writer.WriteLine($"{action}|{path}");
        return true;
    }
}
