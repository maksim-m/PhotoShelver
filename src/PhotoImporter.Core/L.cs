using System.Globalization;

namespace PhotoImporter.Core;

/// <summary>
/// Interface language: Russian when Windows' display language is Russian, English otherwise.
/// Every text is written next to the code that uses it, in both languages: <c>L.T("English", "Русский")</c>.
/// </summary>
public static class L
{
    private static readonly CultureInfo RussianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Chosen once at start-up (the app may override it, tests set it explicitly).</summary>
    public static bool IsRussian { get; set; } = IsRussianCulture(CultureInfo.CurrentUICulture);

    /// <summary>Culture for numbers, dates and month names in texts of the current language.</summary>
    public static CultureInfo Culture => IsRussian ? RussianCulture : EnglishCulture;

    public static string T(string english, string russian) => IsRussian ? russian : english;

    /// <summary>"1 file", "2 files" / "1 файл", "2 файла", "5 файлов".</summary>
    public static string Count(long n, string englishOne, string englishMany, string russianOne, string russianFew, string russianMany)
    {
        if (!IsRussian) return $"{n} {(n == 1 ? englishOne : englishMany)}";
        var mod100 = n % 100;
        var mod10 = n % 10;
        var word = mod100 is >= 11 and <= 14 ? russianMany
            : mod10 == 1 ? russianOne
            : mod10 is >= 2 and <= 4 ? russianFew
            : russianMany;
        return $"{n} {word}";
    }

    public static bool IsRussianCulture(CultureInfo culture) => culture.TwoLetterISOLanguageName == "ru";

    /// <summary>Applies an explicit choice ("ru" / "en"); anything else keeps the system language.</summary>
    public static void Override(string? language)
    {
        if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase)) IsRussian = true;
        else if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)) IsRussian = false;
    }
}
