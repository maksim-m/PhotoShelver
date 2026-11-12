namespace PhotoShelver.Core.Tests;

/// <summary>
/// A test's own folder under %TEMP%\PhotoShelverTests. SAFETY: nothing outside it is ever written or deleted through
/// this class — a path that would leave it (e.g. Path.Combine with an absolute second part) is refused, and cleaning up
/// never follows folder links (junctions, symbolic links) out of it.
/// </summary>
internal sealed class TempDir : IDisposable
{
    /// <summary>The only folder test files are created in.</summary>
    public static string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoShelverTests");

    public TempDir()
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>True when <paramref name="path"/> is inside %TEMP%\PhotoShelverTests.</summary>
    public static bool IsTestPath(string path) => IsInside(path, Root);

    private static bool IsInside(string path, string folder)
    {
        var full = System.IO.Path.GetFullPath(path);
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder)) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private string Checked(string path) => IsInside(path, Path) || string.Equals(System.IO.Path.GetFullPath(path), Path, StringComparison.OrdinalIgnoreCase)
        ? path
        : throw new InvalidOperationException($"SAFETY: test path “{path}” is outside the test folder {Path}");

    public string Sub(params string[] parts)
    {
        var p = Checked(System.IO.Path.Combine([Path, .. parts]));
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content, DateTime? lastWriteUtc = null)
    {
        var p = Checked(System.IO.Path.Combine(Path, relative));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (lastWriteUtc is { } t) System.IO.File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    public void Dispose()
    {
        if (!IsTestPath(Path)) throw new InvalidOperationException($"SAFETY: refusing to delete {Path}");
        // A process that has just exited (the app in UI tests) can hold the folder for a moment: retry briefly.
        for (var attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                ClearReadOnly(Path);
                // Deletes a folder link itself, never what it points to.
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // leftovers in %TEMP% would be harmless anyway
            }
        }
    }

    /// <summary>Makes files deletable, without following folder links out of the test folder.</summary>
    private static void ClearReadOnly(string folder)
    {
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
        foreach (var sub in Directory.EnumerateDirectories(folder, "*", options))
        {
            if (new DirectoryInfo(sub).LinkTarget is not null) continue; // a junction or symbolic link: not ours to touch
            ClearReadOnly(sub);
        }
    }
}
