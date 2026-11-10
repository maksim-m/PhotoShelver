using System.Globalization;
using System.Text;

namespace PhotoShelver.Core;

/// <summary>
/// Folder naming template such as <c>{YYYY}/{MM}-{DD}</c>.
/// Tokens in braces (case-sensitive): {YYYY}, {YY}, {MM}, {MMM}, {DD}. '/' or '\' starts a new folder level.
/// Everything outside braces is literal text.
/// </summary>
public sealed class FolderTemplate
{
    public const string Default = "{YYYY}/{YYYY}-{MM}-{DD}";

    internal static readonly string[] Tokens = ["YYYY", "YY", "MM", "MMM", "DD"];

    internal static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*'];

    internal static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string OnlyInNames(string token) => L.F(Strings.IsForFileNamesOnlyFoldersUse, token, TemplateSyntax.List(Tokens));

    private static readonly Dictionary<string, string> Elsewhere = new()
    {
        ["NAME"] = OnlyInNames("NAME"), ["HH"] = OnlyInNames("HH"), ["ss"] = OnlyInNames("ss"),
        ["mm"] = Strings.MmIsMinutesForFileNamesOnly,
    };

    private const int MaxLength = 200;

    private readonly List<List<TemplateSyntax.Part>> _segments;

    private FolderTemplate(string pattern, List<List<TemplateSyntax.Part>> segments)
    {
        Pattern = pattern;
        _segments = segments;
    }

    public string Pattern { get; }

    /// <summary>Number of folder levels the template produces.</summary>
    public int Depth => _segments.Count;

    /// <summary>Non-blocking remark about a valid but probably unintended template (or null).</summary>
    public string? Warning { get; private set; }

    /// <summary>Ready-made templates offered in the UI.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "{YYYY}/{YYYY}-{MM}-{DD}",
        "{YYYY}/{MM}-{DD}",
        "{YYYY}/{MM}/{DD}",
        "{YYYY}-{MM}-{DD}",
        "{YYYY}/{MM}",
        "{YYYY}/{MMM}",
    ];

    public static FolderTemplate Parse(string pattern) =>
        TryParse(pattern, out var t, out var error) ? t! : throw new FormatException(error);

    public static bool TryParse(string? pattern, out FolderTemplate? template, out string? error)
    {
        template = null;
        error = null;
        pattern = pattern?.Trim() ?? "";
        if (pattern.Length == 0)
        {
            error = Strings.TheTemplateIsEmpty;
            return false;
        }
        if (pattern.Length > MaxLength)
        {
            error = L.F(Strings.TheTemplateIsTooLongMoreThan, MaxLength);
            return false;
        }
        var bad = pattern.IndexOfAny(InvalidChars);
        if (bad >= 0 || pattern.Any(char.IsControl))
        {
            error = Strings.InvalidCharacter + (bad >= 0 ? pattern[bad].ToString() : Strings.ControlCharacter);
            return false;
        }
        if (!TemplateSyntax.TryTokenize(pattern, Tokens, Elsewhere, out var parts, out error))
            return false;

        if (!parts.Any(p => p.IsToken))
        {
            error = TemplateSyntax.BareToken(parts, Tokens) is { } bare
                ? L.F(Strings.TokensAreWrittenInBraces, bare)
                : L.F(Strings.TheTemplateHasNoDateAddAt, TemplateSyntax.List(Tokens));
            return false;
        }

        // Folder levels: literal text is split at '/' and '\'.
        var segments = new List<List<TemplateSyntax.Part>> { new() };
        foreach (var part in parts)
        {
            if (part.IsToken)
            {
                segments[^1].Add(part);
                continue;
            }
            var pieces = part.Text.Split('/', '\\');
            for (var k = 0; k < pieces.Length; k++)
            {
                if (k > 0) segments.Add([]);
                if (pieces[k].Length > 0) segments[^1].Add(TemplateSyntax.Part.Literal(pieces[k]));
            }
        }

        var candidate = new FolderTemplate(pattern, segments);
        foreach (var segment in segments)
        {
            if (segment.Count == 0)
            {
                error = Strings.EmptyFolderLevelExtraSlash;
                return false;
            }
            // Spaces and dots come only from literal text, so a sample date shows them exactly.
            var name = Render(segment, new DateTime(2026, 1, 1));
            if (name.Trim().Length == 0 || name == "." || name == "..")
            {
                error = L.F(Strings.InvalidFolderName, name);
                return false;
            }
            if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
            {
                error = Strings.AFolderNameCannotStartWithA;
                return false;
            }
            if (ReservedNames.Contains(name) || ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            {
                error = L.F(Strings.ReservedWindowsName, name);
                return false;
            }
        }

        var tokens = parts.Where(p => p.IsToken).Select(p => p.Token!).ToHashSet();
        var hasYear = tokens.Contains("YYYY") || tokens.Contains("YY");
        var hasMonth = tokens.Contains("MM") || tokens.Contains("MMM");
        if (TemplateSyntax.BareToken(parts, Tokens) is { } bareToken)
            candidate.Warning = L.F(Strings.WithoutBracesIsPlainTextTheToken, bareToken);
        else if (tokens.Contains("DD") && !hasMonth)
            candidate.Warning = Strings.NoMonthMMOrMMMFilesFrom;
        else if (hasMonth && !hasYear)
            candidate.Warning = Strings.NoYearYYYYOrYYFilesFrom;

        template = candidate;
        return true;
    }

    /// <summary>Returns a relative folder path (levels separated by '\') for the given local date.</summary>
    public string Format(DateTime date) => string.Join('\\', _segments.Select(s => Render(s, date)));

    private static string Render(List<TemplateSyntax.Part> segment, DateTime date)
    {
        var sb = new StringBuilder();
        foreach (var part in segment) sb.Append(part.IsToken ? Value(part.Token!, date) : part.Text);
        return sb.ToString();
    }

    /// <summary>Value of a date or time token (shared with the file name template).</summary>
    internal static string Value(string token, DateTime date) => token switch
    {
        "YYYY" => date.Year.ToString("D4", CultureInfo.InvariantCulture),
        "YY" => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
        "MMM" => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(date.Month),
        "MM" => date.Month.ToString("D2", CultureInfo.InvariantCulture),
        "DD" => date.Day.ToString("D2", CultureInfo.InvariantCulture),
        "HH" => date.Hour.ToString("D2", CultureInfo.InvariantCulture),
        "mm" => date.Minute.ToString("D2", CultureInfo.InvariantCulture),
        "ss" => date.Second.ToString("D2", CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null),
    };
}
