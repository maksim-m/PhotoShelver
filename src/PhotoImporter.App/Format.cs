using System.Globalization;

namespace PhotoImporter.App;

internal static class Format
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", Ru) + " ГБ",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", Ru) + " МБ",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", Ru) + " КБ",
        _ => bytes + " Б",
    };

    public static string Duration(TimeSpan t) => t.TotalSeconds switch
    {
        < 60 => "< 1 мин",
        < 3600 => $"~{Math.Ceiling(t.TotalMinutes)} мин",
        _ => $"~{(int)t.TotalHours} ч {t.Minutes} мин",
    };

    public static string Date(DateTime d) => d.ToString("d MMMM yyyy", Ru);

    public static string DateTime(DateTime d) => d.ToString("d MMMM yyyy, HH:mm:ss", Ru);

    /// <summary>"1 файл", "2 файла", "5 файлов".</summary>
    public static string Files(int n)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? "файлов" : mod10 switch { 1 => "файл", 2 or 3 or 4 => "файла", _ => "файлов" };
        return $"{n} {word}";
    }
}
