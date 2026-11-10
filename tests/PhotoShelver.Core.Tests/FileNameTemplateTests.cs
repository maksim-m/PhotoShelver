namespace PhotoShelver.Core.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime Shot = new(2026, 3, 7, 9, 5, 4);

    [Theory]
    [InlineData("{NAME}", "IMG_2041.JPG", "IMG_2041.JPG")]
    [InlineData("{YYYY}-{MM}-{DD}_{NAME}", "IMG_2041.JPG", "2026-03-07_IMG_2041.JPG")]
    [InlineData("{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}", "IMG_2041.CR3", "20260307_090504_IMG_2041.CR3")]
    [InlineData("{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", "DSC01.ARW", "2026-03-07_09-05-04.ARW")]
    [InlineData("{YY} {MMM} {DD} {NAME}", "a.jpeg", "26 Mar 07 a.jpeg")]
    [InlineData("Trip {NAME}", "IMG_1.HEIC", "Trip IMG_1.HEIC")]                    // literal text around tokens
    [InlineData("Glass_{NAME}", "IMG_1.JPG", "Glass_IMG_1.JPG")]                     // text outside braces is never a token
    [InlineData("MM ss NAME HH {NAME}", "x.JPG", "MM ss NAME HH x.JPG")]
    [InlineData("{NAME}{NAME}", "ab.JPG", "abab.JPG")]                               // a token may repeat
    [InlineData("{ NAME }", "x.JPG", "x.JPG")]                                       // spaces inside braces are ignored
    [InlineData("[{YYYY}] {NAME} (1)", "x.JPG", "[2026] x (1).JPG")]                  // other brackets are plain text
    [InlineData("{YYYY}-{MM}-{DD}_{NAME}", "README", "2026-03-07_README")]           // no extension: none is added
    [InlineData("{YYYY}_{NAME}", "archive.tar.gz", "2026_archive.tar.gz")]           // only the last extension is the extension
    [InlineData("{NAME}_{YYYY}", "img.mp4", "img_2026.mp4")]                         // extension case is kept as is
    [InlineData("{NAME}", ".hidden", ".hidden")]
    [InlineData("{NAME} {ss}", ".JPG", "04.JPG")]                                    // an empty original name leaves no space
    [InlineData("{ss} {NAME}", ".JPG", "04.JPG")]
    [InlineData("{NAME}", "", "")]
    [InlineData("{YYYY}.{NAME}", "x", "2026.x")]
    [InlineData("{NAME}.{YYYY}", "x", "x.2026")]
    [InlineData("  {YYYY}_{NAME}  ", "x.JPG", "2026_x.JPG")]                         // surrounding spaces are trimmed
    [InlineData("Фото {NAME}", "IMG_1.JPG", "Фото IMG_1.JPG")]
    [InlineData("{NAME} - копия", "Отпуск 2026.jpg", "Отпуск 2026 - копия.jpg")]
    public void Formats(string pattern, string original, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Parse(pattern).Format(Shot, original));
    }

    [Fact]
    public void TokensAreCaseSensitive()
    {
        // {MM} is the month, {mm} the minutes; {HH} the hours, {ss} the seconds.
        Assert.Equal("03-05_09-04.JPG", FileNameTemplate.Parse("{MM}-{mm}_{HH}-{ss}_{NAME}").Format(Shot, "a.JPG").Replace("_a", ""));
    }

    [Fact]
    public void TimeIsTwoDigitsAt24Hours()
    {
        var template = FileNameTemplate.Parse("{HH}{mm}{ss}_{NAME}");
        Assert.Equal("000000_a.JPG", template.Format(new DateTime(2026, 1, 1, 0, 0, 0), "a.JPG"));
        Assert.Equal("235959_a.JPG", template.Format(new DateTime(2026, 12, 31, 23, 59, 59), "a.JPG"));
    }

    [Fact]
    public void OnlyExactlyNameKeepsOriginalNames()
    {
        Assert.True(FileNameTemplate.Parse("{NAME}").KeepsOriginalNames);
        Assert.True(FileNameTemplate.Parse(" {NAME} ").KeepsOriginalNames);
        Assert.True(FileNameTemplate.Parse("{ NAME }").KeepsOriginalNames);
        Assert.False(FileNameTemplate.Parse("{NAME}_{YYYY}").KeepsOriginalNames);
        Assert.False(FileNameTemplate.Parse("x {NAME}").KeepsOriginalNames);
        Assert.False(FileNameTemplate.Parse("{NAME}{NAME}").KeepsOriginalNames);
    }

    [Theory]
    [InlineData("", "пустой")]
    [InlineData("   ", "пустой")]
    [InlineData("{YYYY}/{NAME}", "папки задаются")]
    [InlineData(@"{YYYY}\{NAME}", "папки задаются")]
    [InlineData("{YYYY}:{NAME}", "символ")]
    [InlineData("{NAME}?", "символ")]
    [InlineData("{NAME}*", "символ")]
    [InlineData("{NAME}\"", "символ")]
    [InlineData("<{NAME}>", "символ")]
    [InlineData("{NAME}|1", "символ")]
    [InlineData("{NAME}.", "точкой")]
    [InlineData("photo", "нет ни одного токена")]
    [InlineData("NAME", "фигурных скобках: {NAME}")]                 // the old syntax without braces
    [InlineData("YYYY-MM-DD_NAME", "фигурных скобках: {YYYY}")]
    [InlineData("{NAME", "Не хватает «}»")]
    [InlineData("{YYYY{NAME}}", "Не хватает «}»")]
    [InlineData("{{NAME}}", "Не хватает «}»")]
    [InlineData("NAME}", "Лишняя «}»")]
    [InlineData("{NAME}}", "Лишняя «}»")]
    [InlineData("{}", "Пустые скобки")]
    [InlineData("{NAME}{ }", "Пустые скобки")]
    [InlineData("{name}", "Пишется так: {NAME}")]
    [InlineData("{yyyy}_{NAME}", "Пишется так: {YYYY}")]
    [InlineData("{hh}_{NAME}", "Пишется так: {HH}")]
    [InlineData("{SS}_{NAME}", "Пишется так: {ss}")]
    [InlineData("{Mm}_{NAME}", "Непонятный токен {Mm}")]               // {MM} or {mm}? not guessed
    [InlineData("{YYY}_{NAME}", "Непонятный токен {YYY}")]
    [InlineData("{camera}_{NAME}", "Непонятный токен {camera}")]
    [InlineData("CON", "нет ни одного токена")]
    public void RejectsInvalid(string pattern, string messagePart)
    {
        Assert.False(FileNameTemplate.TryParse(pattern, out var template, out var error));
        Assert.Null(template);
        Assert.Contains(messagePart, error);
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(FileNameTemplate.TryParse(null, out _, out var error));
        Assert.Contains("пустой", error);
    }

    [Fact]
    public void RejectsTooLong()
    {
        Assert.False(FileNameTemplate.TryParse("{NAME}" + new string('a', 150), out _, out var error));
        Assert.Contains("длинный", error);
        Assert.True(FileNameTemplate.TryParse("{NAME}" + new string('a', 144), out _, out _)); // exactly 150
    }

    [Fact]
    public void RejectsControlCharacters()
    {
        Assert.False(FileNameTemplate.TryParse("{NAME}\t1", out _, out var error));
        Assert.Contains("управляющий", error);
    }

    [Theory]
    [InlineData("{NAME}")]
    [InlineData("{YYYY}-{MM}-{DD}_{NAME}")]
    [InlineData("{YYYY}{MM}{DD}_{HH}{mm}{ss}_{NAME}")]
    [InlineData("Glass_{NAME}")]
    public void TemplatesWithNameHaveNoWarning(string pattern)
    {
        Assert.Null(FileNameTemplate.Parse(pattern).Warning);
    }

    [Theory]
    [InlineData("{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}", "пары RAW+JPEG")]
    [InlineData("{YYYY}-{MM}-{DD}", "пары RAW+JPEG")]
    [InlineData("{YYYY}-{mm}-{DD}_{NAME}", "{mm} — это минуты")]          // minutes where the month was meant
    [InlineData("{HH}-{MM}_{NAME}", "{MM} после часов — это месяц")]       // month where the minutes were meant
    [InlineData("{YYYY}-MM-{DD}_{NAME}", "«MM» без скобок")]                // a forgotten pair of braces
    [InlineData("{YYYY}_NAME", "«NAME» без скобок")]
    public void Warns(string pattern, string warningPart)
    {
        Assert.Contains(warningPart, FileNameTemplate.Parse(pattern).Warning);
    }

    [Fact]
    public void MonthBeforeHoursIsNotWarnedAbout()
    {
        Assert.Null(FileNameTemplate.Parse("{YYYY}-{MM}-{DD}_{HH}h_{NAME}").Warning);
    }

    [Fact]
    public void AllPresetsAreValid()
    {
        Assert.Equal(FileNameTemplate.Default, FileNameTemplate.Presets[0]);
        Assert.True(FileNameTemplate.Parse(FileNameTemplate.Default).KeepsOriginalNames);
        foreach (var preset in FileNameTemplate.Presets)
            Assert.True(FileNameTemplate.TryParse(preset, out _, out var error), $"{preset}: {error}");
        Assert.All(FileNameTemplate.Presets.Where(p => p.Contains("{NAME}")), p => Assert.Null(FileNameTemplate.Parse(p).Warning));
    }

    /// <summary>Property: whatever is typed, a template that passes validation yields a plain file name for any file.</summary>
    [Fact]
    public void NoValidTemplateProducesAPathOrAnInvalidName()
    {
        var rnd = new Random(2026);
        string[] pieces = ["{NAME}", "{YYYY}", "{YY}", "{MM}", "{MMM}", "{DD}", "{HH}", "{mm}", "{ss}", "{", "}", "NAME",
            "/", "\\", ".", "..", " ", "-", "_", "a", ":", "C:", "~", "%", "CON", "*", "?"];
        string[] originals = ["IMG_1.JPG", "x", ".hidden", "a.b.c", "Отпуск.jpg", "very long name with spaces.MOV"];
        var dates = new[] { new DateTime(2026, 1, 1), new DateTime(1999, 12, 31, 23, 59, 59), new DateTime(2024, 2, 29, 12, 0, 0) };
        var valid = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var pattern = string.Concat(Enumerable.Range(0, rnd.Next(1, 7)).Select(_ => pieces[rnd.Next(pieces.Length)]));
            if (!FileNameTemplate.TryParse(pattern, out var template, out _)) continue;
            valid++;
            foreach (var original in originals)
            foreach (var date in dates)
            {
                var name = template!.Format(date, original);
                Assert.True(name.Length > 0, pattern);
                Assert.Equal(name, Path.GetFileName(name));
                Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, $"{pattern} → {name}");
                Assert.False(name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '), $"{pattern} → “{name}”");
                Assert.EndsWith(Path.GetExtension(original), name);
            }
        }
        Assert.True(valid > 500, $"only {valid} valid templates generated");
    }
}
