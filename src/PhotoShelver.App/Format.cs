using PhotoShelver.Core;

namespace PhotoShelver.App;

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", L.Culture) + L.T(" GB", " ГБ"),
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", L.Culture) + L.T(" MB", " МБ"),
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", L.Culture) + L.T(" KB", " КБ"),
        _ => bytes + L.T(" B", " Б"),
    };

    public static string Duration(TimeSpan t) => t.TotalSeconds switch
    {
        < 60 => L.T("< 1 min", "< 1 мин"),
        < 3600 => L.T($"~{Math.Ceiling(t.TotalMinutes)} min", $"~{Math.Ceiling(t.TotalMinutes)} мин"),
        _ => L.T($"~{(int)t.TotalHours} h {t.Minutes} min", $"~{(int)t.TotalHours} ч {t.Minutes} мин"),
    };

    /// <summary>"18 февраля 2022" / "February 18, 2022".</summary>
    public static string Date(DateTime d) => d.ToString(L.T("MMMM d, yyyy", "d MMMM yyyy"), L.Culture);

    public static string DateTime(DateTime d) => d.ToString(L.T("MMMM d, yyyy, HH:mm:ss", "d MMMM yyyy, HH:mm:ss"), L.Culture);

    /// <summary>"1 файл", "2 файла", "5 файлов" / "1 file", "2 files".</summary>
    public static string Files(int n) => L.Count(n, "file", "files", "файл", "файла", "файлов");
}
