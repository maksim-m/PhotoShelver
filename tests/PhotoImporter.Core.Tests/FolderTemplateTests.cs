namespace PhotoImporter.Core.Tests;

public class FolderTemplateTests
{
    private static readonly DateTime Date = new(2026, 3, 7, 23, 30, 0);

    [Theory]
    [InlineData("YYYY/MM-DD", @"2026\03-07")]
    [InlineData(@"YYYY\MM\DD", @"2026\03\07")]
    [InlineData("YY-MMM", "26-Mar")]
    [InlineData("Photos YYYY/MMM DD", @"Photos 2026\Mar 07")]
    [InlineData("YYYYMMDD", "20260307")]
    [InlineData("My Day YYYY", "My Day 2026")]          // single Y/M/D letters are literal text
    [InlineData("summer mm YYYY", "summer mm 2026")]    // tokens are case-sensitive
    [InlineData("  YYYY/MM  ", @"2026\03")]             // surrounding spaces are trimmed
    public void Formats(string pattern, string expected)
    {
        Assert.Equal(expected, FolderTemplate.Parse(pattern).Format(Date));
    }

    [Theory]
    [InlineData("", "пустой")]
    [InlineData("   ", "пустой")]
    [InlineData("YYYY//MM", "слэш")]
    [InlineData("/YYYY", "слэш")]
    [InlineData("YYYY/", "слэш")]
    [InlineData("YYYY:MM", "символ")]
    [InlineData("YYYY/MM?", "символ")]
    [InlineData("YYYY/..", "Недопустимое имя")]
    [InlineData("YYYY/MM.", "точкой")]
    [InlineData("CON/YYYY", "Зарезервированное")]
    [InlineData("Photos", "нет даты")]
    [InlineData("Imports/Card", "нет даты")]
    [InlineData("yyyy/mm-dd", "заглавными")]
    [InlineData("YYY/MM", "«YYY»")]
    [InlineData("YYYYY", "«YYYYY»")]
    [InlineData("YYYY/MMMM", "«MMMM»")]
    [InlineData("YYYY/MM-DDD", "«DDD»")]
    public void RejectsInvalid(string pattern, string messagePart)
    {
        Assert.False(FolderTemplate.TryParse(pattern, out var template, out var error));
        Assert.Null(template);
        Assert.Contains(messagePart, error);
    }

    [Fact]
    public void RejectsTooLong()
    {
        Assert.False(FolderTemplate.TryParse("YYYY/" + new string('a', 250), out _, out var error));
        Assert.Contains("длинный", error);
    }

    [Theory]
    [InlineData("YYYY/DD", "Нет месяца")]
    [InlineData("DD", "Нет месяца")]
    [InlineData("MM-DD", "Нет года")]
    [InlineData("MMM", "Нет года")]
    [InlineData("YYYY/MM-DD", null)]
    [InlineData("YYYY", null)]
    [InlineData("YY/MMM", null)]
    public void WarnsAboutAmbiguousTemplates(string pattern, string? warningPart)
    {
        var template = FolderTemplate.Parse(pattern); // still valid
        if (warningPart is null) Assert.Null(template.Warning);
        else Assert.Contains(warningPart, template.Warning);
    }

    [Fact]
    public void PresetsAreValidAndUnambiguous()
    {
        Assert.Equal(FolderTemplate.Default, FolderTemplate.Presets[0]); // default is listed first
        Assert.Equal(@"2026\2026-03-07", FolderTemplate.Parse(FolderTemplate.Default).Format(Date));
        Assert.All(FolderTemplate.Presets, p => Assert.Null(FolderTemplate.Parse(p).Warning));
        Assert.Equal(FolderTemplate.Presets.Count, FolderTemplate.Presets.Select(p => FolderTemplate.Parse(p).Format(Date)).Distinct().Count());
    }

    [Fact]
    public void Depth()
    {
        Assert.Equal(2, FolderTemplate.Parse("YYYY/MM-DD").Depth);
        Assert.Equal(3, FolderTemplate.Parse(@"YYYY\MM\DD").Depth);
    }
}
