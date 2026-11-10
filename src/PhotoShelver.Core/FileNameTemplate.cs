using System.Text;

namespace PhotoShelver.Core;

/// <summary>
/// File naming template such as <c>{YYYY}-{MM}-{DD}_{NAME}</c>. Applies to the name without the extension; the original
/// extension is always kept. Tokens in braces (case-sensitive): {YYYY}, {YY}, {MM}, {MMM}, {DD} (date),
/// {HH}, {mm}, {ss} (time), {NAME} (the original name without the extension). Everything outside braces is literal text.
/// The default template "{NAME}" keeps the original names.
/// </summary>
public sealed class FileNameTemplate
{
    public const string Default = "{NAME}";

    internal static readonly string[] Tokens = ["NAME", "YYYY", "YY", "MM", "MMM", "DD", "HH", "mm", "ss"];

    private static readonly Dictionary<string, string> Elsewhere = [];

    private const int MaxLength = 150;

    private readonly List<TemplateSyntax.Part> _parts;

    private FileNameTemplate(string pattern, List<TemplateSyntax.Part> parts)
    {
        Pattern = pattern;
        _parts = parts;
    }

    public string Pattern { get; }

    /// <summary>True when the template keeps the original names (exactly "{NAME}").</summary>
    public bool KeepsOriginalNames => _parts is [{ Token: "NAME" }];

    /// <summary>Non-blocking remark about a valid but probably unintended template (or null).</summary>
    public string? Warning { get; private set; }

    /// <summary>Ready-made templates offered in the UI.</summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "{NAME}",
        "{YYYY}-{MM}-{DD}_{NAME}",
        "{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}",
        "{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}_{NAME}",
        "{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}",
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
        if (!TemplateSyntax.TryTokenize(pattern, Tokens, Elsewhere, out var parts, out error))
            return false;

        if (!parts.Any(p => p.IsToken))
        {
            error = TemplateSyntax.BareToken(parts, Tokens) is { } bare
                ? L.T($"Tokens are written in braces: {{{bare}}}", $"Токены пишутся в фигурных скобках: {{{bare}}}")
                : L.T($"The template has no tokens, so every file would get the same name. Add {{NAME}} or a date: {TemplateSyntax.List(Tokens)}",
                    $"В шаблоне нет ни одного токена — все файлы получили бы одно имя. Добавьте {{NAME}} или дату: {TemplateSyntax.List(Tokens)}");
            return false;
        }

        var candidate = new FileNameTemplate(pattern, parts);
        var sample = Path.GetFileNameWithoutExtension(candidate.Format(new DateTime(2026, 1, 1, 10, 0, 0), "IMG_0001.JPG"));
        if (FolderTemplate.ReservedNames.Contains(sample))
        {
            error = L.T($"Reserved Windows name: “{sample}”", $"Зарезервированное имя Windows: «{sample}»");
            return false;
        }

        candidate.Warning = WarningFor(parts);
        template = candidate;
        return true;
    }

    private static string? WarningFor(List<TemplateSyntax.Part> parts)
    {
        var tokens = parts.Where(p => p.IsToken).Select(p => p.Token!).ToList();
        if (TemplateSyntax.BareToken(parts, Tokens) is { } bare)
            return L.T($"“{bare}” without braces is plain text; the token is written {{{bare}}}",
                $"«{bare}» без скобок — это просто текст; токен пишется {{{bare}}}");
        if (tokens.Contains("mm") && !tokens.Contains("HH"))
            return L.T("{mm} is minutes (without hours); the month is {MM}", "{mm} — это минуты (а часов нет); месяц — {MM}");
        if (tokens.Contains("HH") && tokens.Contains("MM") && !tokens.Contains("mm")
            && tokens.IndexOf("MM") > tokens.IndexOf("HH"))
            return L.T("{MM} after the hours is the month; minutes are {mm}", "{MM} после часов — это месяц; минуты — {mm}");
        if (!tokens.Contains("NAME"))
            return L.T("Without {NAME}, shots taken in the same second (or on the same day, without time) get the same name: _1, _2… are added, and RAW+JPEG or Live Photo pairs may get different numbers",
                "Без {NAME} у снимков одной секунды (или одного дня, если нет времени) имена совпадут: добавятся _1, _2…, и пары RAW+JPEG или Live Photo могут получить разные номера");
        return null;
    }

    /// <summary>
    /// The new file name for a file: the template applied to its capture time (local) and original name,
    /// plus the original extension. Spaces at the ends are dropped (an original name can be empty, e.g. ".JPG"),
    /// and so are trailing dots when there is no extension; an empty result keeps the original name.
    /// </summary>
    public string Format(DateTime local, string originalFileName)
    {
        var sb = new StringBuilder();
        foreach (var part in _parts)
        {
            if (!part.IsToken) sb.Append(part.Text);
            else if (part.Token == "NAME") sb.Append(Path.GetFileNameWithoutExtension(originalFileName));
            else sb.Append(FolderTemplate.Value(part.Token!, local));
        }
        var extension = Path.GetExtension(originalFileName);
        var name = sb.ToString().Trim();
        if (extension.Length == 0) name = name.TrimEnd('.', ' ');
        return name.Length == 0 && extension.Length == 0 ? originalFileName : name + extension;
    }
}
