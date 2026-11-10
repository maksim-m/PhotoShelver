using System.Globalization;
using System.Runtime.CompilerServices;
using PhotoShelver.Core.Importing;

namespace PhotoShelver.Core.Tests;

internal static class TestLanguage
{
    /// <summary>The tests check Russian texts unless they switch the language themselves (see <see cref="LanguageTests"/>).</summary>
    [ModuleInitializer]
    internal static void UseRussian() => L.IsRussian = true;
}

/// <summary>The language is a process-wide setting: these tests switch it, so nothing runs in parallel with them.</summary>
[CollectionDefinition(nameof(LanguageTests), DisableParallelization = true)]
public sealed class LanguageCollection;

[Collection(nameof(LanguageTests))]
public sealed class LanguageTests : IDisposable
{
    public void Dispose() => L.IsRussian = true;

    [Theory]
    [InlineData(0, "0 файлов")]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(4, "4 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(12, "12 файлов")]
    [InlineData(14, "14 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(22, "22 файла")]
    [InlineData(111, "111 файлов")]
    [InlineData(101, "101 файл")]
    public void RussianPlurals(long n, string expected)
    {
        L.IsRussian = true;
        Assert.Equal(expected, L.Count(n, Strings.FileOne, Strings.FileFew, Strings.FileMany));
    }

    [Theory]
    [InlineData(0, "0 files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(21, "21 files")]
    public void EnglishPlurals(long n, string expected)
    {
        L.IsRussian = false;
        Assert.Equal(expected, L.Count(n, Strings.FileOne, Strings.FileFew, Strings.FileMany));
    }

    [Theory]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData("ru", "ru-RU")]        // a neutral culture becomes a specific one (for numbers and dates)
    [InlineData("ru-KZ", "ru-KZ")]     // Russian in another country: Russian texts, that country's number format
    [InlineData("en-US", "en-US")]
    [InlineData("en-GB", "en-US")]     // English is the resource fallback: always en-US
    [InlineData("de-DE", "en-US")]     // no translation: English, also for numbers and dates (no mixed languages)
    [InlineData("uk-UA", "en-US")]
    [InlineData("", "en-US")]          // invariant culture
    public void SystemLanguageDecidesWhenTranslated(string system, string expected)
    {
        Assert.Equal(expected, L.Choose(CultureInfo.GetCultureInfo(system)).Name);
    }

    [Fact]
    public void OnlyShippedTranslationsCount()
    {
        Assert.True(L.IsTranslated("ru"));
        Assert.False(L.IsTranslated("de"));
        Assert.False(L.IsTranslated("xx"));
    }

    [Fact]
    public void OverrideAcceptsOnlyKnownLanguages()
    {
        L.IsRussian = true;
        L.Override("EN");
        Assert.False(L.IsRussian);
        L.Override("ru");
        Assert.True(L.IsRussian);
        L.Override("de");      // not translated
        L.Override("nonsense");
        L.Override("");
        L.Override(null);
        Assert.True(L.IsRussian); // unchanged
        L.Override("ru-RU");
        Assert.Equal("ru-RU", L.Language.Name);
    }

    [Theory]
    [InlineData("ru", 1, "one")]
    [InlineData("ru", 3, "few")]
    [InlineData("ru", 12, "many")]
    [InlineData("ru", 22, "few")]
    [InlineData("pl", 1, "one")]
    [InlineData("pl", 4, "few")]
    [InlineData("pl", 13, "many")]
    [InlineData("pl", 21, "many")] // unlike Russian
    [InlineData("pl", 24, "few")]
    [InlineData("de", 1, "one")]
    [InlineData("de", 3, "many")]
    [InlineData("de", 0, "many")]
    public void PluralRulesFollowTheLanguage(string language, long n, string form)
    {
        L.Language = CultureInfo.CreateSpecificCulture(language);
        Assert.Equal($"{n} {form}", L.Count(n, "one", "few", "many"));
    }

    [Fact]
    public void TextsFollowTheLanguage()
    {
        L.IsRussian = false;
        Assert.Equal("Copy", Strings.Copy);
        L.IsRussian = true;
        Assert.Equal("Копировать", Strings.Copy);
        L.Language = CultureInfo.GetCultureInfo("de-DE"); // no German resources: English texts
        Assert.Equal("Copy", Strings.Copy);
    }

    [Fact]
    public void TemplateErrorsInEnglish()
    {
        L.IsRussian = false;
        Assert.False(FolderTemplate.TryParse("", out _, out var empty));
        Assert.Equal("The template is empty", empty);
        Assert.False(FolderTemplate.TryParse("{yyyy}/{MM}", out _, out var lower));
        Assert.Equal("Write it as {YYYY} (letter case matters)", lower);
        Assert.False(FolderTemplate.TryParse("YYYY/MM", out _, out var bare));
        Assert.Equal("Tokens are written in braces: {YYYY}", bare);
        Assert.False(FolderTemplate.TryParse("{YYYY", out _, out var open));
        Assert.Equal("Missing “}”", open);
        Assert.False(FileNameTemplate.TryParse("{YYYY}/{NAME}", out _, out var slash));
        Assert.StartsWith("A file name cannot contain", slash);
        Assert.True(FileNameTemplate.TryParse("{YYYY}", out var names, out _));
        Assert.StartsWith("Without {NAME}", names!.Warning);
        Assert.True(FolderTemplate.TryParse("{DD}", out var template, out _));
        Assert.StartsWith("No month", template!.Warning);
    }

    [Fact]
    public void FolderNamesDoNotDependOnTheLanguage()
    {
        // Only the interface is translated: the same template must give the same folders in any language.
        var date = new DateTime(2026, 11, 9);
        L.IsRussian = true;
        var russian = FolderTemplate.Parse("{YYYY}/{MMM}-{DD}").Format(date);
        L.IsRussian = false;
        var english = FolderTemplate.Parse("{YYYY}/{MMM}-{DD}").Format(date);
        Assert.Equal(russian, english);
        Assert.Equal(@"2026\Nov-09", english);
    }

    [Fact]
    public void ImportLogInEnglish()
    {
        L.IsRussian = false;
        using var dir = new TempDir();
        var src = dir.File(@"src\OK.JPG", "fine");
        var missing = Path.Combine(dir.Path, @"src\MISSING.JPG");
        var dest = Path.Combine(dir.Path, "dst");
        ImportJob[] jobs = [new(src, dest, 4), new(missing, dest, 10)];
        var log = ImportLog.Start(Path.Combine(dir.Path, "logs"), ImportMode.Copy, @"E:\DCIM", dest, "YYYY", jobs, "1.0.0",
            new DateTime(2026, 11, 9, 14, 30, 12));
        var result = new Importer().Run(jobs, ImportMode.Copy, null, default, log.Record);
        log.Finish(result, new DateTime(2026, 11, 9, 14, 31, 0));

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal("PhotoShelver 1.0.0 — import log", lines[0]);
        Assert.Contains("Mode:     copy", lines);
        Assert.Contains(lines, l => l.StartsWith("[OK]       ") && l.Contains("4 B"));
        Assert.Contains(lines, l => l.StartsWith("[ERROR]    ") && l.Contains("Source file not found"));
        Assert.Contains("Result:   imported 1, errors 1", lines);
    }
}
