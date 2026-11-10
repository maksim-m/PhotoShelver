using System.Text.Json;
using PhotoShelver.Core;

namespace PhotoShelver.App.Services;

public sealed class AppSettings
{
    public string? SourcePath { get; set; }
    public string? TargetPath { get; set; }
    public string Template { get; set; } = FolderTemplate.Default;
    public string NameTemplate { get; set; } = FileNameTemplate.Default;
    public bool IncludeSubfolders { get; set; } = true;
    public bool MoveMode { get; set; } = true;
    public bool ShowFullTree { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }
}

public static class SettingsStore
{
    /// <summary>PHOTOSHELVER_SETTINGS overrides the location (used for testing without touching real settings).</summary>
    private static readonly string FilePath =
        Environment.GetEnvironmentVariable("PHOTOSHELVER_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoShelver", "settings.json");

    /// <summary>Import logs live next to the settings file (in tests that is a temp folder).</summary>
    public static string LogsDirectory => Path.Combine(Path.GetDirectoryName(FilePath)!, "logs");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start with defaults.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not critical.
        }
    }
}
