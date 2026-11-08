using System.Globalization;
using System.Text;

namespace PhotoImporter.Core;

/// <summary>
/// Folder naming template such as <c>YYYY/MM-DD</c>.
/// Tokens (case-sensitive): YYYY, YY, MMM, MM, DD. '/' or '\' starts a new folder level.
/// Everything else is literal text.
/// </summary>
public sealed class FolderTemplate
{
    public const string Default = "YYYY/MM-DD";

    private enum TokenKind { Literal, Year4, Year2, MonthName, Month, Day }

    private readonly record struct Token(TokenKind Kind, string Text = "");

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
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
            error = "Шаблон пустой";
            return false;
        }

        var bad = pattern.IndexOfAny(InvalidChars);
        if (bad >= 0 || pattern.Any(char.IsControl))
        {
            error = $"Недопустимый символ: {(bad >= 0 ? pattern[bad].ToString() : "управляющий символ")}";
            return false;
        }

        var segments = new List<List<Token>>();
        foreach (var raw in pattern.Replace('/', '\\').Split('\\'))
        {
            if (raw.Length == 0)
            {
                error = "Пустой уровень папки (лишний слэш)";
                return false;
            }
            if (raw.Trim().Length == 0 || raw == "." || raw == "..")
            {
                error = $"Недопустимое имя папки: «{raw}»";
                return false;
            }
            if (raw.EndsWith('.') || raw.EndsWith(' ') || raw.StartsWith(' '))
            {
                error = "Имя папки не может начинаться с пробела или заканчиваться пробелом или точкой";
                return false;
            }
            segments.Add(Tokenize(raw));
        }

        var candidate = new FolderTemplate(pattern, segments);
        foreach (var name in candidate.Format(new DateTime(2026, 1, 1)).Split('\\'))
        {
            if (ReservedNames.Contains(name) || ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
            {
                error = $"Зарезервированное имя Windows: «{name}»";
                return false;
            }
        }

        template = candidate;
        return true;
    }

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
