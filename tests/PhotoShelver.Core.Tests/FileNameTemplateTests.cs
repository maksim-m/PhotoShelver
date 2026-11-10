namespace PhotoShelver.Core.Tests;

public class FileNameTemplateTests
{
    private static readonly DateTime Shot = new(2026, 3, 7, 9, 5, 4);

    [Theory]
    [InlineData("NAME", "IMG_2041.JPG", "IMG_2041.JPG")]
    [InlineData("YYYY-MM-DD_NAME", "IMG_2041.JPG", "2026-03-07_IMG_2041.JPG")]
    [InlineData("YYYYMMDD_HHmmss_NAME", "IMG_2041.CR3", "20260307_090504_IMG_2041.CR3")]
    [InlineData("YYYY-MM-DD_HH-mm-ss", "DSC01.ARW", "2026-03-07_09-05-04.ARW")]
    [InlineData("YY MMM DD NAME", "a.jpeg", "26 Mar 07 a.jpeg")]
    [InlineData("Trip NAME", "IMG_1.HEIC", "Trip IMG_1.HEIC")]          // literal text around tokens
    [InlineData("YYYY-MM-DD_NAME", "README", "2026-03-07_README")]       // no extension: none is added
    [InlineData("YYYY-MM-DD_NAME", "archive.tar.gz", "2026-03-07_archive.tar.gz")] // only the last extension is kept
    [InlineData("NAME_YYYY", "img.mp4", "img_2026.mp4")]                 // extension case is kept as is
    [InlineData("  YYYY_NAME  ", "x.JPG", "2026_x.JPG")]                 // surrounding spaces are trimmed
    public void Formats(string pattern, string original, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Parse(pattern).Format(Shot, original));
    }

    [Fact]
    public void TokensAreCaseSensitive()
    {
        // MM is the month, mm the minutes; HH the hours, ss the seconds.
        Assert.Equal("03-05_09-04.JPG", FileNameTemplate.Parse("MM-mm_HH-ss").Format(Shot, "a.JPG"));
    }

    [Fact]
    public void OnlyExactlyNameKeepsOriginalNames()
    {
        Assert.True(FileNameTemplate.Parse("NAME").KeepsOriginalNames);
        Assert.True(FileNameTemplate.Parse(" NAME ").KeepsOriginalNames);
        Assert.False(FileNameTemplate.Parse("NAME_YYYY").KeepsOriginalNames);
        Assert.False(FileNameTemplate.Parse("x NAME").KeepsOriginalNames);
    }

    [Theory]
    [InlineData("", "пустой")]
    [InlineData("   ", "пустой")]
    [InlineData("YYYY/NAME", "папки задаются")]
    [InlineData(@"YYYY\NAME", "папки задаются")]
    [InlineData("YYYY:NAME", "символ")]
    [InlineData("NAME?", "символ")]
    [InlineData("NAME*", "символ")]
    [InlineData("NAME.", "точкой")]
    [InlineData("photo", "нет ни одного токена")]
    [InlineData("name", "нет ни одного токена")]                 // tokens are written in capitals
    [InlineData("YYY_NAME", "«YYY»")]
    [InlineData("YYYY-MMMM_NAME", "«MMMM»")]
    [InlineData("DDD_NAME", "«DDD»")]
    [InlineData("HHH_NAME", "«HHH»")]
    [InlineData("NAME_mmm", "«mmm»")]
    [InlineData("NAME_sss", "«sss»")]
    public void RejectsInvalid(string pattern, string messagePart)
    {
        Assert.False(FileNameTemplate.TryParse(pattern, out var template, out var error));
        Assert.Null(template);
        Assert.Contains(messagePart, error);
    }

    [Fact]
    public void RejectsTooLong()
    {
        Assert.False(FileNameTemplate.TryParse("NAME" + new string('a', 100), out _, out var error));
        Assert.Contains("длинный", error);
    }

    [Fact]
    public void RejectsControlCharacters()
    {
        Assert.False(FileNameTemplate.TryParse("NAME\t1", out _, out var error));
        Assert.Contains("управляющий", error);
    }

    [Theory]
    [InlineData("NAME")]
    [InlineData("YYYY-MM-DD_NAME")]
    [InlineData("YYYYMMDD_HHmmss_NAME")]
    public void TemplatesWithNameHaveNoWarning(string pattern)
    {
        Assert.Null(FileNameTemplate.Parse(pattern).Warning);
    }

    [Theory]
    [InlineData("YYYY-MM-DD_HH-mm-ss")]
    [InlineData("YYYY-MM-DD")]
    public void WarnsThatWithoutNamePairsMaySplit(string pattern)
    {
        Assert.Contains("пары RAW+JPEG", FileNameTemplate.Parse(pattern).Warning);
    }

    [Theory]
    [InlineData("yyyy-MM-dd_NAME")]
    [InlineData("YYYY-MM-DD_hh_NAME")]
    public void WarnsAboutLowercaseDateLetters(string pattern)
    {
        Assert.Contains("заглавными", FileNameTemplate.Parse(pattern).Warning);
    }

    [Fact]
    public void AllPresetsAreValid()
    {
        Assert.Equal(FileNameTemplate.Default, FileNameTemplate.Presets[0]);
        foreach (var preset in FileNameTemplate.Presets)
            Assert.True(FileNameTemplate.TryParse(preset, out _, out var error), $"{preset}: {error}");
    }

    [Fact]
    public void ResultIsAlwaysAPlainFileName()
    {
        // Whatever the original name, a valid template never produces a path.
        foreach (var preset in FileNameTemplate.Presets)
        {
            var name = FileNameTemplate.Parse(preset).Format(Shot, "IMG_1.JPG");
            Assert.Equal(name, Path.GetFileName(name));
            Assert.True(name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0, name);
        }
    }
}
