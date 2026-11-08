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
    [InlineData("summer mm", "summer mm")] // tokens are case-sensitive
    public void Formats(string pattern, string expected)
    {
        Assert.Equal(expected, FolderTemplate.Parse(pattern).Format(Date));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YYYY//MM")]
    [InlineData("/YYYY")]
    [InlineData("YYYY/")]
    [InlineData("YYYY:MM")]
    [InlineData("YYYY/MM?")]
    [InlineData("YYYY/..")]
    [InlineData("YYYY/MM.")]
    [InlineData("CON/YYYY")]
    public void RejectsInvalid(string pattern)
    {
        Assert.False(FolderTemplate.TryParse(pattern, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Depth()
    {
        Assert.Equal(2, FolderTemplate.Parse("YYYY/MM-DD").Depth);
        Assert.Equal(3, FolderTemplate.Parse(@"YYYY\MM\DD").Depth);
    }
}
