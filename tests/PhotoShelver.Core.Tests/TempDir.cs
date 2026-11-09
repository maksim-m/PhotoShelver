namespace PhotoShelver.Core.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoShelverTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public string File(string relative, string content, DateTime? lastWriteUtc = null)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (lastWriteUtc is { } t) System.IO.File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    public void Dispose()
    {
        // A process that has just exited (the app in UI tests) can hold the folder for a moment: retry briefly.
        for (var attempt = 0; attempt < 5 && Directory.Exists(Path); attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    System.IO.File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // leftovers in %TEMP% would be harmless anyway
            }
        }
    }
}
