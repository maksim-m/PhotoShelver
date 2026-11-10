using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;

namespace PhotoShelver.Core.Tests;

/// <summary>
/// The texts in Strings.resx / Strings.ru.resx: every language has every text, with the same {0}, {1}… so that
/// string.Format never throws in some rarely shown message, and every text is used by the code.
/// </summary>
[Collection(nameof(LanguageTests))]
public sealed partial class ResourceTests : IDisposable
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public void Dispose() => L.IsRussian = true;

    private static Dictionary<string, string> Texts(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"no resources for “{culture.Name}”");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static Dictionary<string, string> English => Texts(CultureInfo.InvariantCulture);
    private static Dictionary<string, string> RussianTexts => Texts(Russian);

    [GeneratedRegex(@"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static SortedSet<int> Placeholders(string text) =>
        new(Placeholder().Matches(text.Replace("{{", "")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)));

    [Fact]
    public void EveryLanguageHasEveryText()
    {
        var english = English;
        var russian = RussianTexts;
        Assert.True(english.Count > 150, $"only {english.Count} texts");
        Assert.Empty(english.Keys.Except(russian.Keys));
        Assert.Empty(russian.Keys.Except(english.Keys));
        Assert.All(english, e => Assert.False(string.IsNullOrEmpty(e.Value), e.Key));
        Assert.All(russian, e => Assert.False(string.IsNullOrEmpty(e.Value), e.Key));
    }

    [Fact]
    public void PlaceholdersMatchAndEveryFormatWorks()
    {
        var russian = RussianTexts;
        foreach (var (key, en) in English)
        {
            var ru = russian[key];
            var expected = Placeholders(en);
            Assert.True(expected.SetEquals(Placeholders(ru)), $"{key}: “{en}” vs “{ru}”");
            if (expected.Count == 0) continue;
            // {0}…{n} without gaps, and both texts are valid format strings (escaped braces included).
            Assert.Equal(Enumerable.Range(0, expected.Max + 1), expected);
            var args = Enumerable.Range(0, expected.Max + 1).Select(i => (object)$"<{i}>").ToArray();
            foreach (var text in new[] { en, ru })
            {
                var formatted = string.Format(CultureInfo.InvariantCulture, text, args);
                Assert.All(args, a => Assert.Contains((string)a, formatted));
            }
        }
    }

    [Fact]
    public void RussianTextsAreTranslated()
    {
        var russian = RussianTexts;
        string[] datePatterns = [nameof(Strings.LongDateFormat), nameof(Strings.LongDateTimeFormat)]; // not words
        foreach (var (key, en) in English.Where(e => !datePatterns.Contains(e.Key)))
        {
            // Words outside tokens ({NAME}, {0}) must be Russian in the Russian text.
            var words = TokenOrPlaceholder().Replace(en, "");
            if (!LatinWord().IsMatch(words)) continue;
            Assert.True(Cyrillic().IsMatch(russian[key]), $"{key}: “{russian[key]}” is not translated");
        }
    }

    [GeneratedRegex(@"\{+[^{}]*\}+")]
    private static partial Regex TokenOrPlaceholder();

    [GeneratedRegex("[A-Za-z]{2,}")]
    private static partial Regex LatinWord();

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex Cyrillic();

    [Fact]
    public void GeneratedPropertiesFollowTheLanguage()
    {
        // Through the generated class (what the app uses), for every text: the resource is found in both languages.
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).ToList();
        var english = English;
        var russian = RussianTexts;
        Assert.Equal(english.Count, properties.Count);

        L.IsRussian = false;
        Assert.All(properties, p => Assert.Equal(english[p.Name], (string)p.GetValue(null)!));
        L.IsRussian = true;
        Assert.All(properties, p => Assert.Equal(russian[p.Name], (string)p.GetValue(null)!));
    }

    [Fact]
    public void EveryTextIsUsedByTheCode()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PhotoShelver.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var code = string.Join("\n", Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
        var unused = English.Keys.Where(k => !Regex.IsMatch(code, $@"\bStrings\.{k}\b")).ToList();
        Assert.Empty(unused);
    }
}
