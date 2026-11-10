namespace PhotoShelver.Core.Tests;

public class FolderTemplateTests
{
    private static readonly DateTime Date = new(2026, 3, 7, 23, 30, 0);

    [Theory]
    [InlineData("{YYYY}/{MM}-{DD}", @"2026\03-07")]
    [InlineData(@"{YYYY}\{MM}\{DD}", @"2026\03\07")]
    [InlineData("{YY}-{MMM}", "26-Mar")]
    [InlineData("Photos {YYYY}/{MMM} {DD}", @"Photos 2026\Mar 07")]
    [InlineData("{YYYY}{MM}{DD}", "20260307")]                    // adjacent tokens
    [InlineData("My Day {YYYY}", "My Day 2026")]
    [InlineData("summer mm {YYYY}", "summer mm 2026")]
    [InlineData("Glass DDR {YYYY}", "Glass DDR 2026")]             // text outside braces is never a token
    [InlineData("MMMM {YYYY}", "MMMM 2026")]
    [InlineData("  {YYYY}/{MM}  ", @"2026\03")]                   // surrounding spaces are trimmed
    [InlineData("{ YYYY }/{MM}", @"2026\03")]                     // spaces inside braces are ignored
    [InlineData("{YYYY}/{YYYY}-{MM}-{DD}", @"2026\2026-03-07")]
    [InlineData("Imports/{YYYY}", @"Imports\2026")]               // a level of plain text
    [InlineData("{YYYY}/Card 1", @"2026\Card 1")]
    [InlineData("{YYYY}.{MM}", "2026.03")]
    [InlineData("{YYYY}/.hidden", @"2026\.hidden")]                // a dot at the start is allowed
    [InlineData("[{YYYY}] (#1) {MM}", "[2026] (#1) 03")]           // other brackets are plain text
    [InlineData("{YYYY}/Фото {MM}", @"2026\Фото 03")]
    public void Formats(string pattern, string expected)
    {
        Assert.Equal(expected, FolderTemplate.Parse(pattern).Format(Date));
    }

    [Theory]
    [InlineData("", "пустой")]
    [InlineData("   ", "пустой")]
    [InlineData("{YYYY}//{MM}", "лишний слэш")]
    [InlineData("/{YYYY}", "лишний слэш")]
    [InlineData("{YYYY}/", "лишний слэш")]
    [InlineData(@"{YYYY}\/{MM}", "лишний слэш")]
    [InlineData("{YYYY}:{MM}", "символ")]
    [InlineData("{YYYY}/{MM}?", "символ")]
    [InlineData("{YYYY}/<{MM}>", "символ")]
    [InlineData("{YYYY}/..", "Недопустимое имя")]
    [InlineData("{YYYY}/.", "Недопустимое имя")]
    [InlineData("{YYYY}/ /{MM}", "Недопустимое имя")]
    [InlineData("{YYYY}/{MM}.", "точкой")]
    [InlineData("{YYYY} /{MM}", "пробел")]
    [InlineData("{YYYY}/ {MM}", "пробел")]
    [InlineData("CON/{YYYY}", "Зарезервированное")]
    [InlineData("{YYYY}/nul.txt", "Зарезервированное")]
    [InlineData("{YYYY}/LPT1", "Зарезервированное")]
    [InlineData("Photos", "нет даты")]
    [InlineData("Imports/Card", "нет даты")]
    [InlineData("YYYY/MM-DD", "фигурных скобках: {YYYY}")]       // the old syntax without braces
    [InlineData("Photos MM", "фигурных скобках: {MM}")]
    [InlineData("{YYYY", "Не хватает «}»")]
    [InlineData("{YYYY}/{MM", "Не хватает «}»")]
    [InlineData("{YYYY{MM}}", "Не хватает «}»")]
    [InlineData("{{YYYY}}", "Не хватает «}»")]
    [InlineData("YYYY}", "Лишняя «}»")]
    [InlineData("{YYYY}}", "Лишняя «}»")]
    [InlineData("{}", "Пустые скобки")]
    [InlineData("{YYYY}/{ }", "Пустые скобки")]
    [InlineData("{yyyy}/{MM}", "Пишется так: {YYYY}")]
    [InlineData("{YYYY}/{dd}", "Пишется так: {DD}")]
    [InlineData("{YYYY}/{mmm}", "Пишется так: {MMM}")]
    [InlineData("{YYYY}/{mm}", "месяц — {MM}")]                  // minutes: a file name token
    [InlineData("{YYYY}/{NAME}", "только для имён")]
    [InlineData("{YYYY}/{HH}", "только для имён")]
    [InlineData("{YYY}/{MM}", "Непонятный токен {YYY}")]
    [InlineData("{YYYY}/{MMMM}", "Непонятный токен {MMMM}")]
    [InlineData("{YYYY}/{Mm}", "Пишется так: {MM}")]
    [InlineData("{YYYY}/{date}", "Непонятный токен {date}")]
    [InlineData("{YYYY}/{YYYY/MM}", "Непонятный токен")]
    public void RejectsInvalid(string pattern, string messagePart)
    {
        Assert.False(FolderTemplate.TryParse(pattern, out var template, out var error));
        Assert.Null(template);
        Assert.Contains(messagePart, error);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(FolderTemplate.TryParse(null, out _, out var error));
        Assert.Contains("пустой", error);
    }

    [Fact]
    public void RejectsTooLong()
    {
        Assert.False(FolderTemplate.TryParse("{YYYY}/" + new string('a', 250), out _, out var error));
        Assert.Contains("длинный", error);
        Assert.True(FolderTemplate.TryParse("{YYYY}/" + new string('a', 193), out _, out _)); // exactly 200
    }

    [Fact]
    public void RejectsControlCharacters()
    {
        Assert.False(FolderTemplate.TryParse("{YYYY}\t{MM}", out _, out var error));
        Assert.Contains("управляющий", error);
    }

    [Fact]
    public void ParseThrowsWithTheMessage()
    {
        var ex = Assert.Throws<FormatException>(() => FolderTemplate.Parse("{YYYY"));
        Assert.Contains("Не хватает", ex.Message);
    }

    [Theory]
    [InlineData("{YYYY}/{DD}", "Нет месяца")]
    [InlineData("{DD}", "Нет месяца")]
    [InlineData("{MM}-{DD}", "Нет года")]
    [InlineData("{MMM}", "Нет года")]
    [InlineData("{YYYY}/MM-{DD}", "«MM» без скобок")]              // a forgotten pair of braces
    [InlineData("{YYYY}/{MM}-DD", "«DD» без скобок")]
    [InlineData("{YYYY}/{MM}-{DD}", null)]
    [InlineData("{YYYY}", null)]
    [InlineData("{YY}/{MMM}", null)]
    [InlineData("DDR {YYYY}/{MM}", null)]                          // DDR is a word, not a token
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
        Assert.Equal(2, FolderTemplate.Parse("{YYYY}/{MM}-{DD}").Depth);
        Assert.Equal(3, FolderTemplate.Parse(@"{YYYY}\{MM}\{DD}").Depth);
        Assert.Equal(1, FolderTemplate.Parse("{YYYY}-{MM}").Depth);
    }

    [Fact]
    public void PatternIsKeptAsTyped()
    {
        Assert.Equal("{YYYY}/{MM}", FolderTemplate.Parse("  {YYYY}/{MM} ").Pattern);
    }

    [Fact]
    public void EveryMonthAndDayIsFormattedWithTwoDigits()
    {
        var template = FolderTemplate.Parse("{YYYY}/{MM}-{DD} {MMM} {YY}");
        Assert.Equal(@"0099\01-01 Jan 99", template.Format(new DateTime(99, 1, 1)));
        Assert.Equal(@"2000\12-31 Dec 00", template.Format(new DateTime(2000, 12, 31)));
        Assert.Equal(@"2024\02-29 Feb 24", template.Format(new DateTime(2024, 2, 29)));
    }
}
