using System.Globalization;
using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Importing;

/// <summary>How the second copy is laid out.</summary>
public enum BackupLayout
{
    /// <summary>A folder per import ("2026-11-10 14-32-05"), with the source's folders and original names.</summary>
    PerImport,

    /// <summary>The same folders and names as in the library (a mirror of the import).</summary>
    Mirror,
}

/// <summary>Where the second copy of an import goes.</summary>
/// <param name="Root">The folder chosen for second copies.</param>
/// <param name="SourceRoot">The import's source folder (per-import layout keeps the folders below it).</param>
/// <param name="SessionFolder">Per-import layout: this import's folder inside <paramref name="Root"/> (see <see cref="ImportPlan.NewSessionFolder"/>).</param>
public sealed record BackupPlan(string Root, BackupLayout Layout, string SourceRoot, string? SessionFolder = null);

/// <summary>Turns selected source files into import jobs (the single place that decides where a file goes).</summary>
public static class ImportPlan
{
    /// <summary>Destination folder of a file: target root + template applied to the capture date.</summary>
    public static string DestinationFolder(string targetRoot, FolderTemplate template, SourceFile file)
    {
        if (file.Date is not { } date)
            throw new InvalidOperationException(L.T($"No date for file {file.Path}", $"Нет даты для файла {file.Path}"));
        return Inside(targetRoot, template.Format(date.Local));
    }

    /// <summary>Name of a file in its destination folder: the name template applied, or the original name.</summary>
    public static string DestinationName(FileNameTemplate? names, SourceFile file)
    {
        if (names is null || names.KeepsOriginalNames) return file.FileName;
        if (file.Date is not { } date)
            throw new InvalidOperationException(L.T($"No date for file {file.Path}", $"Нет даты для файла {file.Path}"));
        return names.Format(date.Local, file.FileName);
    }

    /// <summary>
    /// A folder for this import's second copies inside <paramref name="root"/>, named by the start time
    /// ("2026-11-10 14-32-05"); " (2)", " (3)"… when that name already exists. Not created here.
    /// </summary>
    public static string NewSessionFolder(string root, DateTime started)
    {
        var name = started.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var folder = Inside(root, n == 1 ? name : $"{name} ({n})");
            if (!Directory.Exists(folder) && !File.Exists(folder)) return folder;
        }
    }

    /// <summary>Folder of a file's second copy.</summary>
    public static string BackupFolder(BackupPlan backup, FolderTemplate template, SourceFile file)
    {
        if (backup.Layout == BackupLayout.Mirror)
            return DestinationFolder(backup.Root, template, file);

        var session = backup.SessionFolder ?? throw new InvalidOperationException("No folder for this import's second copies");
        session = Inside(backup.Root, Path.GetRelativePath(PathUtil.WithTrailingSeparator(backup.Root), Path.GetFullPath(session)));
        var relative = Path.GetRelativePath(PathUtil.WithTrailingSeparator(backup.SourceRoot), Path.GetDirectoryName(file.Path)!);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException(L.T($"File {file.Path} is outside the source folder", $"Файл {file.Path} вне папки-источника"));
        return relative == "." ? session : Inside(session, relative);
    }

    /// <summary>Jobs for files that can be imported (readable, with a date), in the given order.</summary>
    public static List<ImportJob> CreateJobs(IEnumerable<SourceFile> files, string targetRoot, FolderTemplate template,
        FileNameTemplate? names = null, BackupPlan? backup = null) =>
        files
            .Where(f => f.Error is null && f.Date is not null)
            .Select(f =>
            {
                var name = DestinationName(names, f);
                return backup is null
                    ? new ImportJob(f.Path, DestinationFolder(targetRoot, template, f), f.Size, name)
                    : new ImportJob(f.Path, DestinationFolder(targetRoot, template, f), f.Size, name,
                        BackupFolder(backup, template, f),
                        backup.Layout == BackupLayout.Mirror ? name : f.FileName,
                        BackupReusesIdentical: backup.Layout == BackupLayout.Mirror);
            })
            .ToList();

    /// <summary><paramref name="relative"/> under <paramref name="root"/>; anything that would leave the root is refused (defence in depth).</summary>
    private static string Inside(string root, string relative)
    {
        var prefix = PathUtil.WithTrailingSeparator(root); // "E:\" and "E:\Photos\" alike
        var folder = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!folder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || folder.Length <= prefix.Length)
            throw new InvalidOperationException(L.T($"Folder “{folder}” is outside the target folder", $"Папка «{folder}» вне папки-цели"));
        return folder;
    }
}
