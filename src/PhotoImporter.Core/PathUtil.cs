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

    /// <summary>
    /// True when both paths are on the same volume (so a move is an atomic rename).
    /// Uses the volume mount point, so a disk mounted into a folder (C:\Mount\Disk2) counts as a different volume.
    /// When in doubt returns false: the caller then copies and verifies, which is always safe.
    /// </summary>
    public static bool IsSameVolume(string a, string b)
    {
        var va = VolumeOf(a);
        var vb = VolumeOf(b);
        return va is not null && vb is not null && string.Equals(va, vb, StringComparison.OrdinalIgnoreCase);
    }

    private static string? VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        // GetVolumePathName needs an existing path: walk up to the nearest existing ancestor.
        var existing = full;
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
            if (existing is null) return null;
        }
        var buffer = new char[32768];
        return GetVolumePathNameW(existing, buffer, (uint)buffer.Length)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0'))
            : null;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, [System.Runtime.InteropServices.Out] char[] volumePathName, uint bufferLength);

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
