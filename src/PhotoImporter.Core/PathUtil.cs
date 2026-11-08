namespace PhotoImporter.Core;

public static class PathUtil
{
    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;

    /// <summary>True when the paths are the same folder or one contains the other.</summary>
    public static bool AreNestedOrEqual(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na.StartsWith(nb, StringComparison.OrdinalIgnoreCase) || nb.StartsWith(na, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>"IMG_0001.JPG", 2 → "IMG_0001_2.JPG".</summary>
    public static string WithSuffix(string fileName, int n) =>
        n == 0 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)}_{n}{Path.GetExtension(fileName)}";

    public static long? AvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Checks whether the file can be opened for writing (detects write-protected media) without modifying it.</summary>
    public static bool IsWritable(string filePath)
    {
        try
        {
            using var _ = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
