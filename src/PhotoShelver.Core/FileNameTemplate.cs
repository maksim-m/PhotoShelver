using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoShelver.Core;

/// <summary>
/// File naming template such as <c>YYYY-MM-DD_NAME</c>. Applies to the name without the extension; the original
/// extension is always kept. Tokens (case-sensitive): YYYY, YY, MMM, MM, DD (date), HH, mm, ss (time),
/// NAME (the original name without the extension). Everything else is literal text.
/// The default template "NAME" keeps the original names.
/// </summary>
public sealed partial class FileNameTemplate
{
    public const string Default = "NAME";

    private enum TokenKind { Literal, Year4, Year2, MonthName, Month, Day, Hour, Minute, Second, Name }

    private readonly record struct Token(TokenKind Kind, string Text = "");

    private const int MaxLength = 100;

    private readonly List<Token> _tokens;

    private FileNameTemplate(string pattern, List<Token> tokens)
    {
        Pattern = pattern;
        _tokens = tokens;
    }

    public string Pattern { get; }

    /// <summary>True when the template keeps the original names (exactly "NAME").</summary>
    public bool KeepsOriginalNames => _tokens is [{ Kind: TokenKind.Name }];

    /// <summary>Non-blocking remark about a valid but probably unintended template (or null).</summary>
    public string? Warning { get; private set; }

    /// <summary>Ready-made templates offered in the UI.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "NAME",
        "YYYY-MM-DD_NAME",
        "YYYYMMDD_HHmmss_NAME",
        "YYYY-MM-DD_HH-mm-ss_NAME",
        "YYYY-MM-DD_HH-mm-ss",
    ];

    public static FileNameTemplate Parse(string pattern) =>
        TryParse(pattern, out var t, out var error) ? t! : throw new FormatException(error);

    public static bool TryParse(string? pattern, out FileNameTemplate? template, out string? error)
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
        if (pattern.IndexOfAny(['/', '\\']) >= 0)
        {
            error = L.T("A file name cannot contain / or \\ — folders are set by the folder template",
                "В имени файла не может быть / и \\ — папки задаются шаблоном папок");
            return false;
        }
        var bad = pattern.IndexOfAny(FolderTemplate.InvalidChars);
        if (bad >= 0 || pattern.Any(char.IsControl))
        {
            error = L.T("Invalid character: ", "Недопустимый символ: ") + (bad >= 0 ? pattern[bad].ToString() : L.T("control character", "управляющий символ"));
            return false;
        }
        if (pattern.EndsWith('.'))
        {
            error = L.T("A file name cannot end with a dot", "Имя файла не может заканчиваться точкой");
            return false;
        }

        // Runs like YYY, MMMM, DDD, HHH, mmm or sss are almost certainly typos.
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
                error = L.T($"Unknown token “{run.Value}”. Available: {Tokens}", $"Непонятный токен «{run.Value}». Доступны: {Tokens}");
                return false;
            }
        }

        var tokens = Tokenize(pattern);
        if (tokens.All(t => t.Kind == TokenKind.Literal))
        {
            error = L.T($"The template has no tokens, so every file would get the same name. Add NAME or a date: {Tokens}",
                $"В шаблоне нет ни одного токена — все файлы получили бы одно имя. Добавьте NAME или дату: {Tokens}");
            return false;
        }

        var candidate = new FileNameTemplate(pattern, tokens);
        var sample = Path.GetFileNameWithoutExtension(candidate.Format(new DateTime(2026, 1, 1, 10, 0, 0), "IMG_0001.JPG"));
        if (FolderTemplate.ReservedNames.Contains(sample))
        {
            error = L.T($"Reserved Windows name: “{sample}”", $"Зарезервированное имя Windows: «{sample}»");
            return false;
        }

        if (LowercaseDateTokens().IsMatch(pattern))
            candidate.Warning = L.T("Lowercase yy, dd and hh are plain text: the year, day and hours are written in capitals (YYYY, DD, HH)",
                "Строчные yy, dd и hh — это просто текст: год, день и часы пишутся заглавными (YYYY, DD, HH)");
        else if (tokens.All(t => t.Kind != TokenKind.Name))
            candidate.Warning = L.T("Without NAME, shots taken in the same second (or on the same day, without time) get the same name: _1, _2… are added, and RAW+JPEG or Live Photo pairs may get different numbers",
                "Без NAME у снимков одной секунды (или одного дня, если нет времени) имена совпадут: добавятся _1, _2…, и пары RAW+JPEG или Live Photo могут получить разные номера");

        template = candidate;
        return true;
    }

    private const string Tokens = "NAME, YYYY, YY, MM, MMM, DD, HH, mm, ss";

    [GeneratedRegex("Y+|M+|D+|H+|m+|s+")]
    private static partial Regex TokenLetterRuns();

    [GeneratedRegex("yy|dd|hh")]
    private static partial Regex LowercaseDateTokens();

    /// <summary>
    /// The new file name for a file: the template applied to its capture time (local) and original name,
    /// plus the original extension.
    /// </summary>
    public string Format(DateTime local, string originalFileName)
    {
        var sb = new StringBuilder();
        foreach (var token in _tokens)
        {
            sb.Append(token.Kind switch
            {
                TokenKind.Year4 => local.Year.ToString("D4", CultureInfo.InvariantCulture),
                TokenKind.Year2 => (local.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.MonthName => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(local.Month),
                TokenKind.Month => local.Month.ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.Day => local.Day.ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.Hour => local.Hour.ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.Minute => local.Minute.ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.Second => local.Second.ToString("D2", CultureInfo.InvariantCulture),
                TokenKind.Name => Path.GetFileNameWithoutExtension(originalFileName),
                _ => token.Text,
            });
        }
        return sb + Path.GetExtension(originalFileName);
    }

    private static List<Token> Tokenize(string pattern)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var (kind, length) = MatchToken(pattern.AsSpan(i));
            if (kind == TokenKind.Literal)
            {
                literal.Append(pattern[i]);
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
        if (s.StartsWith("NAME")) return (TokenKind.Name, 4);
        if (s.StartsWith("YYYY")) return (TokenKind.Year4, 4);
        if (s.StartsWith("YY")) return (TokenKind.Year2, 2);
        if (s.StartsWith("MMM")) return (TokenKind.MonthName, 3);
        if (s.StartsWith("MM")) return (TokenKind.Month, 2);
        if (s.StartsWith("DD")) return (TokenKind.Day, 2);
        if (s.StartsWith("HH")) return (TokenKind.Hour, 2);
        if (s.StartsWith("mm")) return (TokenKind.Minute, 2);
        if (s.StartsWith("ss")) return (TokenKind.Second, 2);
        return (TokenKind.Literal, 1);
    }
}
