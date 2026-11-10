using PhotoShelver.Core.Scanning;

namespace PhotoShelver.Core.Importing;

/// <summary>Turns selected source files into import jobs (the single place that decides where a file goes).</summary>
public static class ImportPlan
{
    /// <summary>Destination folder of a file: target root + template applied to the capture date.</summary>
    public static string DestinationFolder(string targetRoot, FolderTemplate template, SourceFile file)
    {
        if (file.Date is not { } date)
            throw new InvalidOperationException(L.T($"No date for file {file.Path}", $"Нет даты для файла {file.Path}"));
        var root = PathUtil.WithTrailingSeparator(targetRoot); // "E:\" and "E:\Photos\" alike
        var folder = Path.GetFullPath(Path.Combine(root, template.Format(date.Local)));

        // Defence in depth: a validated template can never leave the target root.
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase) || folder.Length <= root.Length)
            throw new InvalidOperationException(L.T($"Folder “{folder}” is outside the target folder", $"Папка «{folder}» вне папки-цели"));
        return folder;
    }

    /// <summary>Name of a file in its destination folder: the name template applied, or the original name.</summary>
    public static string DestinationName(FileNameTemplate? names, SourceFile file)
    {
        if (names is null || names.KeepsOriginalNames) return file.FileName;
        if (file.Date is not { } date)
            throw new InvalidOperationException(L.T($"No date for file {file.Path}", $"Нет даты для файла {file.Path}"));
        return names.Format(date.Local, file.FileName);
    }

    /// <summary>Jobs for files that can be imported (readable, with a date), in the given order.</summary>
    public static List<ImportJob> CreateJobs(IEnumerable<SourceFile> files, string targetRoot, FolderTemplate template,
        FileNameTemplate? names = null) =>
        files
            .Where(f => f.Error is null && f.Date is not null)
            .Select(f => new ImportJob(f.Path, DestinationFolder(targetRoot, template, f), f.Size, DestinationName(names, f)))
            .ToList();
}
