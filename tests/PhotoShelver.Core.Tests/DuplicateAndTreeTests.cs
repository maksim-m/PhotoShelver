using PhotoShelver.Core.Hashing;
using PhotoShelver.Core.Planning;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Tests;

public class DuplicateAndTreeTests
{
    private static readonly FolderTemplate Template = FolderTemplate.Parse("{YYYY}/{MM}-{DD}");

    private static SourceFile Src(string path, DateTime date)
    {
        var fi = new FileInfo(path);
        return new SourceFile
        {
            Path = fi.FullName, Size = fi.Length, LastWriteTimeUtc = fi.LastWriteTimeUtc, Kind = MediaKind.Photo,
            Date = new CaptureDate(date, DateSource.Metadata),
        };
    }

    [Fact]
    public void DetectsDuplicatesInDestinationFolderOnly()
    {
        using var dir = new TempDir();
        var day = new DateTime(2026, 11, 8, 10, 0, 0);
        dir.File(@"target\2026\11-08\existing.jpg", "AAAA");
        dir.File(@"target\2026\11-07\other-day.jpg", "BBBB"); // same content as b.jpg, but in another folder
        dir.File(@"target\2026\11-08\same-size.jpg", "CCCX");

        var a = Src(dir.File(@"src\a.jpg", "AAAA"), day);
        var b = Src(dir.File(@"src\b.jpg", "BBBB"), day);
        var c = Src(dir.File(@"src\c.jpg", "CCCC"), day);

        var result = DuplicateDetector.Detect([a, b, c], System.IO.Path.Combine(dir.Path, "target"), Template, new HashCache(), null, default);

        Assert.Equal(DuplicateState.InTarget, result[a].State);
        Assert.EndsWith("existing.jpg", result[a].OtherPath);
        Assert.Equal(DuplicateState.New, result[b].State);
        Assert.Equal(DuplicateState.New, result[c].State);
    }

    [Fact]
    public void DetectsDuplicatesInsideSource()
    {
        using var dir = new TempDir();
        var day = new DateTime(2026, 11, 8);
        var a = Src(dir.File(@"src\1\a.jpg", "SAME"), day);
        var b = Src(dir.File(@"src\2\a.jpg", "SAME"), day);
        var c = Src(dir.File(@"src\c.jpg", "DIFF"), day);

        var result = DuplicateDetector.Detect([b, a, c], null, null, new HashCache(), null, default);

        Assert.Equal(DuplicateState.New, result[a].State);
        Assert.Equal(DuplicateState.DuplicateInSource, result[b].State);
        Assert.Equal(a.Path, result[b].OtherPath);
        Assert.Equal(DuplicateState.New, result[c].State);
    }

    [Fact]
    public void SourceDuplicateOfImportedFileIsInTarget()
    {
        using var dir = new TempDir();
        var day = new DateTime(2026, 11, 8);
        dir.File(@"target\2026\11-08\x.jpg", "SAME");
        var a = Src(dir.File(@"src\1\a.jpg", "SAME"), day);
        var b = Src(dir.File(@"src\2\a.jpg", "SAME"), day);

        var result = DuplicateDetector.Detect([a, b], System.IO.Path.Combine(dir.Path, "target"), Template, new HashCache(), null, default);

        Assert.Equal(DuplicateState.InTarget, result[a].State);
        Assert.Equal(DuplicateState.InTarget, result[b].State);
    }

    [Fact]
    public void BuildsTreeWithNewFoldersAndCounts()
    {
        using var dir = new TempDir();
        dir.Sub("2026", "11-05");
        dir.Sub("2026", "11-07");

        var nodes = FolderTree.Build(dir.Path, [@"2026\11-07", @"2026\11-07", @"2026\11-08", @"2025\12-31"], includeExisting: false, existingDepth: 2);

        Assert.Equal(["2025", "2026"], nodes.Select(n => n.Name));
        var y2025 = nodes[0];
        Assert.True(y2025.IsNew);
        Assert.Equal(1, y2025.Count);
        var y2026 = nodes[1];
        Assert.False(y2026.IsNew);
        Assert.Equal(3, y2026.Count);
        Assert.Equal(["11-07", "11-08"], y2026.Children.Select(n => n.Name));
        Assert.False(y2026.Children[0].IsNew);
        Assert.Equal(2, y2026.Children[0].Count);
        Assert.True(y2026.Children[1].IsNew);
    }

    [Fact]
    public void FullTreeIncludesExistingFolders()
    {
        using var dir = new TempDir();
        dir.Sub("2026", "11-05");
        dir.Sub("2026", "11-05", "too-deep");

        var nodes = FolderTree.Build(dir.Path, [@"2026\11-08"], includeExisting: true, existingDepth: 2);

        var y = Assert.Single(nodes);
        Assert.Equal(["11-05", "11-08"], y.Children.Select(n => n.Name));
        Assert.Equal(0, y.Children[0].Count);
        Assert.Empty(y.Children[0].Children);
    }

    [Fact]
    public void ListsExistingFoldersToTheTemplateDepthWithoutHiddenOnes()
    {
        using var dir = new TempDir();
        dir.Sub("2025", "2025-12-31", "too-deep");
        dir.Sub("2026", "2026-01-02");
        var hidden = dir.Sub("2026", ".hidden");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);

        var folders = FolderTree.ListExisting(dir.Path, 2);

        Assert.Equal([@"2025", @"2025\2025-12-31", @"2026", @"2026\2026-01-02"], folders.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(folders.IndexOf("2026") < folders.IndexOf(@"2026\2026-01-02")); // parents first
        Assert.Equal(["2025", "2026"], FolderTree.ListExisting(dir.Path, 1).Order());
        Assert.Empty(FolderTree.ListExisting(Path.Combine(dir.Path, "missing"), 2));
    }

    [Fact]
    public void ATreeFromAKeptListingIsTheSameAsFromTheDisk()
    {
        using var dir = new TempDir();
        dir.Sub("2025", "2025-12-31");
        dir.Sub("2026", "2026-01-02");
        string[] destinations = [@"2026\2026-01-02", @"2026\2026-08-14", @"2026\2026-08-14", @"2027\2027-01-01"];

        var fromDisk = FolderTree.Build(dir.Path, destinations, includeExisting: true, existingDepth: 2);
        var fromListing = FolderTree.Build(dir.Path, destinations, includeExisting: true, existingDepth: 2,
            existing: FolderTree.ListExisting(dir.Path, 2));

        static IEnumerable<string> Flatten(IEnumerable<FolderNode> nodes) =>
            nodes.SelectMany(n => Flatten(n.Children).Prepend($"{n.FullPath}|{n.Count}|{n.IsNew}"));
        Assert.Equal(Flatten(fromDisk), Flatten(fromListing));
    }

    [Fact]
    public void NewDestinationsAreCheckedOnTheDiskEvenWithAnOldListing()
    {
        using var dir = new TempDir();
        var listing = FolderTree.ListExisting(dir.Path, 2); // empty: taken before the folder existed
        dir.Sub("2026", "2026-08-14");

        var nodes = FolderTree.Build(dir.Path, [@"2026\2026-08-14"], includeExisting: true, existingDepth: 2, existing: listing);

        var day = Assert.Single(Assert.Single(nodes).Children);
        Assert.False(day.IsNew); // exists now, although the listing did not have it
        Assert.False(nodes[0].IsNew);
    }

    [Fact]
    public void Nesting()
    {
        Assert.True(PathUtil.AreNestedOrEqual(@"C:\Photos", @"C:\Photos\"));
        Assert.True(PathUtil.AreNestedOrEqual(@"C:\Photos", @"c:\photos\import"));
        Assert.True(PathUtil.AreNestedOrEqual(@"C:\Photos\import", @"C:\Photos"));
        Assert.False(PathUtil.AreNestedOrEqual(@"C:\Photos", @"C:\Photos2"));
    }
}
