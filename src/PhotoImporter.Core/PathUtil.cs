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

    /// <summary>
    /// Whether the file could be deleted after a move: false for a read-only volume (e.g. an SD card with the
    /// lock switch on) or a file with the read-only attribute. Nothing is opened for writing.
    /// Note: opening a file for writing is NOT a valid test - Windows allows it on read-only volumes and only
    /// refuses the actual write.
    /// </summary>
    public static bool IsWritable(string filePath)
    {
        try
        {
            if (File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly)) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return !IsReadOnlyVolume(filePath);
    }

    /// <summary>True when the volume holding <paramref name="path"/> is mounted read-only.</summary>
    public static bool IsReadOnlyVolume(string path)
    {
        const uint fileReadOnlyVolume = 0x00080000;
        var volume = VolumeOf(path);
        if (volume is null) return false;
        return GetVolumeInformationW(volume, null, 0, out _, out _, out var flags, null, 0)
               && (flags & fileReadOnlyVolume) != 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, char[]? volumeNameBuffer, uint volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        char[]? fileSystemNameBuffer, uint fileSystemNameSize);
}
