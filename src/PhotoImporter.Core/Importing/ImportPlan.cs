using PhotoImporter.Core.Scanning;

namespace PhotoImporter.Core.Importing;

/// <summary>Turns selected source files into import jobs (the single place that decides where a file goes).</summary>
public static class ImportPlan
{
    /// <summary>Destination folder of a file: target root + template applied to the capture date.</summary>
    public static string DestinationFolder(string targetRoot, FolderTemplate template, SourceFile file)
    {
        if (file.Date is not { } date)
            throw new InvalidOperationException($"Нет даты для файла {file.Path}");
        var root = Path.GetFullPath(targetRoot);
        var folder = Path.GetFullPath(Path.Combine(root, template.Format(date.Local)));

        // Defence in depth: a validated template can never leave the target root.
        if (!folder.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Папка «{folder}» вне папки-цели");
        return folder;
    }

    /// <summary>Jobs for files that can be imported (readable, with a date), in the given order.</summary>
    public static List<ImportJob> CreateJobs(IEnumerable<SourceFile> files, string targetRoot, FolderTemplate template) =>
        files
            .Where(f => f.Error is null && f.Date is not null)
            .Select(f => new ImportJob(f.Path, DestinationFolder(targetRoot, template, f), f.Size))
            .ToList();
}
