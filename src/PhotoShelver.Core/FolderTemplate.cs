using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoShelver.Core;

/// <summary>
/// Folder naming template such as <c>YYYY/MM-DD</c>.
/// Tokens (case-sensitive): YYYY, YY, MMM, MM, DD. '/' or '\' starts a new folder level.
/// Everything else is literal text.
/// </summary>
public sealed partial class FolderTemplate
{
    public const string Default = "YYYY/YYYY-MM-DD";

    private enum TokenKind { Literal, Year4, Year2, MonthName, Month, Day }

    private readonly record struct Token(TokenKind Kind, string Text = "");

    internal static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*'];

    internal static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly List<List<Token>> _segments;

    private FolderTemplate(string pattern, List<List<Token>> segments)
    {
        Pattern = pattern;
        _segments = segments;
    }

    public string Pattern { get; }

    /// <summary>Number of folder levels the template produces.</summary>
    public int Depth => _segments.Count;

    public static bool TryParse(string? pattern, out FolderTemplate? template, out string? error)
    {
        template = null;
        error = null;
        pattern = pattern?.Trim() ?? "";
        if (pattern.Length == 0)
        {
            error = L.T("The template is empty", "Шаблон пустой");
            return false;
        }

        if (pattern.Length > MaxLength)
        {
            error = L.T($"The template is too long (more than {MaxLength} characters)", $"Шаблон слишком длинный (больше {MaxLength} символов)");
            return false;
        }

        var bad = pattern.IndexOfAny(InvalidChars);
        if (bad >= 0 || pattern.Any(char.IsControl))
        {
            error = L.T("Invalid character: ", "Недопустимый символ: ") + (bad >= 0 ? pattern[bad].ToString() : L.T("control character", "управляющий символ"));
            return false;
        }

        // Runs like YYY, MMMM or DDD are almost certainly typos (they would render as "26Y", "NovM", "08D").
        foreach (Match run in TokenLetterRuns().Matches(pattern))
        {
            var ok = run.Value[0] switch
            {
                'Y' => run.Length is 1 or 2 or 4,
                'M' => run.Length is 1 or 2 or 3,
                _ => run.Length is 1 or 2,
            };
            if (!ok)
            {
                error = L.T($"Unknown token “{run.Value}”. Available: YYYY, YY, MM, MMM, DD", $"Непонятный токен «{run.Value}». Доступны: YYYY, YY, MM, MMM, DD");
                return false;
            }
        }

        var segments = new List<List<Token>>();
        foreach (var raw in pattern.Replace('/', '\\').Split('\\'))
        {
            if (raw.Length == 0)
            {
                error = L.T("Empty folder level (extra slash)", "Пустой уровень папки (лишний слэш)");
                return false;
            }
            if (raw.Trim().Length == 0 || raw == "." || raw == "..")
            {
                error = L.T($"Invalid folder name: “{raw}”", $"Недопустимое имя папки: «{raw}»");
                return false;
            }
            if (raw.EndsWith('.') || raw.EndsWith(' ') || raw.StartsWith(' '))
            {
                error = L.T("A folder name cannot start with a space or end with a space or a dot", "Имя папки не может начинаться с пробела или заканчиваться пробелом или точкой");
                return false;
            }
            segments.Add(Tokenize(raw));
        }

        var kinds = segments.SelectMany(s => s).Select(t => t.Kind).Where(k => k != TokenKind.Literal).ToHashSet();
        if (kinds.Count == 0)
        {
            error = LowercaseTokens().IsMatch(pattern)
                ? L.T("Tokens are written in capitals: YYYY, YY, MM, MMM, DD", "Токены пишутся заглавными буквами: YYYY, YY, MM, MMM, DD")
                : L.T("The template has no date. Add at least one token: YYYY, YY, MM, MMM, DD", "В шаблоне нет даты. Добавьте хотя бы один токен: YYYY, YY, MM, MMM, DD");
            return false;
        }

        var candidate = new FolderTemplate(pattern, segments);
        foreach (var name in candidate.Format(new DateTime(2026, 1, 1)).Split('\\'))
        {
            if (ReservedNames.Contains(name) || ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            {
                error = L.T($"Reserved Windows name: “{name}”", $"Зарезервированное имя Windows: «{name}»");
                return false;
            }
        }

        var hasYear = kinds.Contains(TokenKind.Year4) || kinds.Contains(TokenKind.Year2);
        var hasMonth = kinds.Contains(TokenKind.Month) || kinds.Contains(TokenKind.MonthName);
        if (kinds.Contains(TokenKind.Day) && !hasMonth)
            candidate.Warning = L.T("No month (MM or MMM): files from the same day of different months share a folder", "Нет месяца (MM или MMM): в одну папку попадут файлы одного числа разных месяцев");
        else if (hasMonth && !hasYear)
            candidate.Warning = L.T("No year (YYYY or YY): files from the same month of different years share a folder", "Нет года (YYYY или YY): в одну папку попадут файлы одного месяца разных лет");

        template = candidate;
        return true;
    }

    /// <summary>Non-blocking remark about a valid but probably unintended template (or null).</summary>
    public string? Warning { get; private set; }

    /// <summary>Ready-made templates offered in the UI.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "YYYY/YYYY-MM-DD",
        "YYYY/MM-DD",
        "YYYY/MM/DD",
        "YYYY-MM-DD",
        "YYYY/MM",
        "YYYY/MMM",
    ];

    private const int MaxLength = 200;

    [GeneratedRegex("Y+|M+|D+")]
    private static partial Regex TokenLetterRuns();

    [GeneratedRegex("yyyy|yy|mm|dd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LowercaseTokens();

    public static FolderTemplate Parse(string pattern) =>
        TryParse(pattern, out var t, out var error) ? t! : throw new FormatException(error);

    /// <summary>Returns a relative folder path (levels separated by '\') for the given local date.</summary>
    public string Format(DateTime date)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < _segments.Count; i++)
        {
            if (i > 0) sb.Append('\\');
            foreach (var token in _segments[i])
            {
                sb.Append(token.Kind switch
                {
                    TokenKind.Year4 => date.Year.ToString("D4", CultureInfo.InvariantCulture),
                    TokenKind.Year2 => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                    TokenKind.MonthName => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(date.Month),
                    TokenKind.Month => date.Month.ToString("D2", CultureInfo.InvariantCulture),
                    TokenKind.Day => date.Day.ToString("D2", CultureInfo.InvariantCulture),
                    _ => token.Text,
                });
            }
        }
        return sb.ToString();
    }

    private static List<Token> Tokenize(string segment)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < segment.Length)
        {
            var (kind, length) = MatchToken(segment.AsSpan(i));
            if (kind == TokenKind.Literal)
            {
                literal.Append(segment[i]);
                i++;
                continue;
            }
            if (literal.Length > 0)
            {
                tokens.Add(new Token(TokenKind.Literal, literal.ToString()));
                literal.Clear();
            }
            tokens.Add(new Token(kind));
            i += length;
        }
        if (literal.Length > 0) tokens.Add(new Token(TokenKind.Literal, literal.ToString()));
        return tokens;
    }

    private static (TokenKind, int) MatchToken(ReadOnlySpan<char> s)
    {
        if (s.StartsWith("YYYY")) return (TokenKind.Year4, 4);
        if (s.StartsWith("YY")) return (TokenKind.Year2, 2);
        if (s.StartsWith("MMM")) return (TokenKind.MonthName, 3);
        if (s.StartsWith("MM")) return (TokenKind.Month, 2);
        if (s.StartsWith("DD")) return (TokenKind.Day, 2);
        return (TokenKind.Literal, 1);
    }
}
