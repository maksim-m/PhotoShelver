using System.Text.RegularExpressions;

namespace PhotoShelver.Core.Importing;

/// <summary>
/// Temp files left behind by an interrupted import ("&lt;name&gt;.&lt;hex id&gt;.importing").
/// They are always redundant: a source is deleted only after its temp file was renamed into place,
/// so for every leftover the original is still where it was imported from.
/// </summary>
public static partial class ImportLeftovers
{
    /// <summary>Our temp-name pattern: 8 hex digits (current) or 32 (older builds).</summary>
    [GeneratedRegex(@"^.+\.([0-9a-f]{8}|[0-9a-f]{32})\.importing$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static bool IsLeftover(string fileName) => Pattern().IsMatch(fileName);

    /// <summary>Finds leftovers anywhere under <paramref name="root"/> (inaccessible folders are skipped).</summary>
    public static List<FileInfo> Find(string root, CancellationToken ct = default)
    {
        var result = new List<FileInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var file in PathUtil.EnumerateFiles(root, recursive: true, skip: 0, pattern: "*.importing"))
        {
            ct.ThrowIfCancellationRequested();
            if (IsLeftover(file.Name)) result.Add(file);
        }
        return result;
    }

    /// <summary>Deletes the given leftovers; anything not matching the pattern is refused. Returns files that could not be deleted.</summary>
    public static List<(string Path, string Error)> Delete(IEnumerable<string> paths)
    {
        var failed = new List<(string, string)>();
        foreach (var path in paths)
        {
            if (!IsLeftover(Path.GetFileName(path)))
            {
                failed.Add((path, Strings.NotAnImportTempFileNotDeleted));
                continue;
            }
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((path, ex.Message));
            }
        }
        return failed;
    }
}
