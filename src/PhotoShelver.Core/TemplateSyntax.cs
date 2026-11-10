using System.Text;
using System.Text.RegularExpressions;

namespace PhotoShelver.Core;

/// <summary>
/// Shared syntax of the folder and file name templates: tokens are written in braces, e.g. <c>{YYYY}-{MM}-{DD}</c>;
/// everything outside braces is literal text. Token names are case-sensitive (<c>{MM}</c> month, <c>{mm}</c> minutes).
/// Braces cannot be used as literal text.
/// </summary>
internal static partial class TemplateSyntax
{
    /// <summary>A piece of a template: a token (its name, without braces) or literal text.</summary>
    internal readonly record struct Part(string? Token, string Text)
    {
        public bool IsToken => Token is not null;
        public static Part Literal(string text) => new(null, text);
        public static Part Of(string token) => new(token, "");
    }

    /// <param name="allowed">Token names valid in this template.</param>
    /// <param name="elsewhere">Token names valid only in the other template, with the explanation shown for them.</param>
    public static bool TryTokenize(string pattern, IReadOnlyCollection<string> allowed, IReadOnlyDictionary<string, string> elsewhere,
        out List<Part> parts, out string? error)
    {
        parts = [];
        error = null;
        var literal = new StringBuilder();
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '}')
            {
                error = L.T("Extra “}” without “{”", "Лишняя «}» без «{»");
                return false;
            }
            if (c != '{')
            {
                literal.Append(c);
                i++;
                continue;
            }

            var close = pattern.IndexOf('}', i + 1);
            var nextOpen = pattern.IndexOf('{', i + 1);
            if (close < 0 || (nextOpen >= 0 && nextOpen < close))
            {
                error = L.T("Missing “}”", "Не хватает «}»");
                return false;
            }
            var name = pattern[(i + 1)..close].Trim();
            if (!allowed.Contains(name))
            {
                error = UnknownToken(name, allowed, elsewhere);
                return false;
            }
            if (literal.Length > 0)
            {
                parts.Add(Part.Literal(literal.ToString()));
                literal.Clear();
            }
            parts.Add(Part.Of(name));
            i = close + 1;
        }
        if (literal.Length > 0) parts.Add(Part.Literal(literal.ToString()));
        return true;
    }

    private static string UnknownToken(string name, IReadOnlyCollection<string> allowed, IReadOnlyDictionary<string, string> elsewhere)
    {
        if (name.Length == 0)
            return L.T("Empty braces {}", "Пустые скобки {}");
        if (elsewhere.TryGetValue(name, out var explanation))
            return explanation;
        var sameLetters = allowed.Where(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameLetters.Count == 1)
            return L.T($"Write it as {{{sameLetters[0]}}} (letter case matters)", $"Пишется так: {{{sameLetters[0]}}} (регистр букв важен)");
        return L.T($"Unknown token {{{name}}}. Available: {List(allowed)}", $"Непонятный токен {{{name}}}. Доступны: {List(allowed)}");
    }

    public static string List(IEnumerable<string> tokens) => string.Join(", ", tokens.Select(t => "{" + t + "}"));

    /// <summary>Token names written without braces in literal text (probably meant as tokens), or null.</summary>
    public static string? BareToken(IEnumerable<Part> parts, IReadOnlyCollection<string> names)
    {
        foreach (var part in parts.Where(p => !p.IsToken))
        {
            foreach (Match m in UppercaseRuns().Matches(part.Text))
                if (names.Contains(m.Value)) return m.Value;
        }
        return null;
    }

    [GeneratedRegex("[A-Z]+")]
    private static partial Regex UppercaseRuns();
}
