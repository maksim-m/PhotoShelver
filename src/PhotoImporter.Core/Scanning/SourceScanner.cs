using PhotoImporter.Core.Metadata;

namespace PhotoImporter.Core.Scanning;

public sealed class SourceFile
{
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required DateTime LastWriteTimeUtc { get; init; }
    public required MediaKind Kind { get; init; }

    /// <summary>Set after metadata reading (null until then).</summary>
    public CaptureDate? Date { get; set; }

    /// <summary>Non-null when the file could not be read.</summary>
    public string? Error { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record EnumerationResult(IReadOnlyList<SourceFile> Files, int SkippedOther);

public static class SourceScanner
{
    public static EnumerationResult Enumerate(string root, bool recursive, CancellationToken ct = default)
    {
        var files = new List<SourceFile>();
        var skipped = 0;
        foreach (var info in PathUtil.EnumerateFiles(root, recursive, FileAttributes.Hidden | FileAttributes.System))
        {
            ct.ThrowIfCancellationRequested();
            if (!MediaTypes.TryGetKind(info.Name, out var kind))
            {
                skipped++;
                continue;
            }
            files.Add(new SourceFile
            {
                Path = info.FullName,
                Size = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc,
                Kind = kind,
            });
        }
        return new EnumerationResult(files, skipped);
    }

    public static async Task ReadMetadataAsync(
        IReadOnlyList<SourceFile> files,
        CaptureDateReader reader,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var done = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (file, _) =>
        {
            try
            {
                if (!File.Exists(file.Path)) throw new FileNotFoundException(L.T("File not found", "Файл не найден"), file.Path);
                file.Date = reader.Read(file.Path, file.Kind, file.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                file.Error = ex.Message;
            }
            progress?.Report(Interlocked.Increment(ref done));
            return ValueTask.CompletedTask;
        });
    }
}
