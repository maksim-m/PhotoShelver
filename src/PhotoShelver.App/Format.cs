using PhotoShelver.Core;

namespace PhotoShelver.App;

internal static class Format
{
    public static string Bytes(long bytes) => L.Bytes(bytes);

    public static string Duration(TimeSpan t) => t.TotalSeconds switch
    {
        < 60 => Strings.T1Min,
        < 3600 => L.F(Strings.DurationMinutes, Math.Ceiling(t.TotalMinutes)),
        _ => L.F(Strings.DurationHoursMinutes, (int)t.TotalHours, t.Minutes),
    };

    /// <summary>"18 февраля 2022" / "February 18, 2022".</summary>
    public static string Date(DateTime d) => d.ToString(Strings.LongDateFormat, L.Culture);

    public static string DateTime(DateTime d) => d.ToString(Strings.LongDateTimeFormat, L.Culture);

    /// <summary>"1 файл", "2 файла", "5 файлов" / "1 file", "2 files".</summary>
    public static string Files(int n) => L.Count(n, Strings.FileOne, Strings.FileFew, Strings.FileMany);
}
