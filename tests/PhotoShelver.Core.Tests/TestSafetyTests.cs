using System.Diagnostics;

namespace PhotoShelver.Core.Tests;

/// <summary>The test folders themselves: a test can never write or delete outside %TEMP%\PhotoShelverTests through them.</summary>
public class TestSafetyTests
{
    [Theory]
    [InlineData(@"C:\Windows\photoshelver-test.txt")]   // an absolute path wins in Path.Combine
    [InlineData(@"..\escape.txt")]
    [InlineData(@"sub\..\..\escape.txt")]
    public void ATestFolderRefusesPathsOutsideItself(string relative)
    {
        using var dir = new TempDir();
        Assert.Throws<InvalidOperationException>(() => dir.File(relative, "x"));
        Assert.Throws<InvalidOperationException>(() => dir.Sub(relative));
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(dir.Path, relative))));
    }

    [Fact]
    public void TestFoldersAreUnderTheTestRoot()
    {
        using var dir = new TempDir();
        Assert.True(TempDir.IsTestPath(dir.Path));
        Assert.True(TempDir.IsTestPath(Path.Combine(dir.Path, "a", "b")));
        Assert.False(TempDir.IsTestPath(TempDir.Root));                              // the root itself is not a test folder
        Assert.False(TempDir.IsTestPath(TempDir.Root + "Other"));                     // a sibling with the same prefix
        Assert.False(TempDir.IsTestPath(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        Assert.False(TempDir.IsTestPath(Path.Combine(dir.Path, "..", "..", "..")));
    }

    [Fact]
    public void CleaningUpNeverFollowsAFolderLinkOut()
    {
        // "outside" stands for a folder that is not the test's own: a junction to it must be removed as a link only.
        using var outside = new TempDir();
        var precious = outside.File("photo.jpg", "precious");
        File.SetAttributes(precious, FileAttributes.ReadOnly);
        try
        {
            var dir = new TempDir();
            var link = Path.Combine(dir.Path, "link");
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside.Path}\"") { UseShellExecute = false, CreateNoWindow = true }))
                p!.WaitForExit();
            Assert.True(File.Exists(Path.Combine(link, "photo.jpg")));

            dir.Dispose();

            Assert.False(Directory.Exists(dir.Path));
            Assert.Equal("precious", File.ReadAllText(precious));                           // still there
            Assert.True(File.GetAttributes(precious).HasFlag(FileAttributes.ReadOnly));      // and untouched
        }
        finally
        {
            File.SetAttributes(precious, FileAttributes.Normal);
        }
    }
}
