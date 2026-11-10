using System.Globalization;

namespace PhotoShelver.Core;

/// <summary>
/// Interface language. The texts live in Strings.resx (English, also the fallback) and Strings.&lt;language&gt;.resx
/// (Strings.ru.resx…); <see cref="Strings"/> is generated from them. The language is Windows' display language when
/// there is a translation for it, English otherwise — adding a language is adding one Strings.xx.resx file.
/// A text with values is a format string: <c>L.F(Strings.CopiedTo, path)</c>.
/// </summary>
public static class L
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static CultureInfo _language = English;

    static L() => Language = Choose(CultureInfo.CurrentUICulture);

    /// <summary>The language of the texts (a specific culture, e.g. ru-RU); chosen at start-up, tests set it explicitly.</summary>
    public static CultureInfo Language
    {
        get => _language;
        set
        {
            _language = value;
            Strings.Culture = value; // texts missing in a translation fall back to English
        }
    }

    /// <summary>Shortcut for tests and checks of the Russian translation.</summary>
    public static bool IsRussian
    {
        get => Language.TwoLetterISOLanguageName == "ru";
        set => Language = value ? CultureInfo.GetCultureInfo("ru-RU") : English;
    }

    /// <summary>Culture for numbers, dates and month names in texts: the language's own.</summary>
    public static CultureInfo Culture => Language;

    /// <summary>A text with values: <paramref name="format"/> has {0}, {1}… (formatted like C# interpolation).</summary>
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>
    /// "1 file", "2 files" / "1 файл", "2 файла", "5 файлов": the three forms come from the resources and the
    /// language's plural rule picks one (English and most languages use only "one" and "many").
    /// </summary>
    public static string Count(long n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        var word = Language.TwoLetterISOLanguageName switch
        {
            // Russian, Ukrainian, Belarusian: 1, 21 → one; 2–4, 22–24 → few; 0, 5–20, 25–30 → many.
            "ru" or "uk" or "be" => mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many,
            // Polish: 1 → one; 2–4, 22–24 (not 12–14) → few; everything else → many.
            "pl" => n == 1 ? one : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? few : many,
            _ => n == 1 ? one : many,
        };
        return $"{n} {word}";
    }

    /// <summary>The language to use for a system language: itself when translated, English otherwise.</summary>
    public static CultureInfo Choose(CultureInfo system)
    {
        var language = system.TwoLetterISOLanguageName;
        if (language is "en" or "iv" || !IsTranslated(language)) return English;
        return system.IsNeutralCulture ? CultureInfo.CreateSpecificCulture(system.Name) : system;
    }

    /// <summary>Whether Strings.&lt;language&gt;.resx exists (its satellite assembly is there).</summary>
    public static bool IsTranslated(string twoLetterLanguage)
    {
        try
        {
            return Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(twoLetterLanguage), createIfNotExists: true, tryParents: false) is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Applies an explicit choice ("ru", "en", "ru-RU"…); an unknown or untranslated language keeps the current one.</summary>
    public static void Override(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return;
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language.Trim());
        }
        catch (CultureNotFoundException)
        {
            return;
        }
        if (culture.TwoLetterISOLanguageName == "en") Language = English;
        else if (IsTranslated(culture.TwoLetterISOLanguageName)) Language = Choose(culture);
    }
}
