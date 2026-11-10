using PhotoShelver.Core.Hashing;

namespace PhotoShelver.Core.Scanning;

public enum DuplicateState
{
    New,

    /// <summary>A file with identical content exists in the destination folder.</summary>
    InTarget,

    /// <summary>Another source file has identical content; only that one is imported.</summary>
    DuplicateInSource,
}

/// <param name="OtherPath">For InTarget: the existing file in the target. For DuplicateInSource: the primary source file.</param>
public sealed record DuplicateInfo(DuplicateState State, string? OtherPath = null);

public static class DuplicateDetector
{
    /// <summary>
    /// Classifies source files. Files with errors or without a date are ignored.
    /// The target check only looks at the top level of the destination folder computed by the template.
    /// </summary>
    public static Dictionary<SourceFile, DuplicateInfo> Detect(
        IReadOnlyList<SourceFile> files,
        string? targetRoot,
        FolderTemplate? template,
        HashCache cache,
        IProgress<(int Done, int Total)>? progress,
        CancellationToken ct)
    {
        var candidates = files.Where(f => f.Error is null && f.Date is not null).ToList();
        var result = candidates.ToDictionary(f => f, _ => new DuplicateInfo(DuplicateState.New));
        var total = candidates.Count * 2;
        var done = 0;

        UInt128? SourceHash(SourceFile f)
        {
            try
            {
                return cache.GetOrCompute(f.Path, f.Size, f.LastWriteTimeUtc, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                f.Error = ex.Message;
                return null;
            }
        }

        // 1. Duplicates inside the source (only files sharing a size need hashing).
        foreach (var sizeGroup in candidates.GroupBy(f => f.Size).Where(g => g.Count() > 1))
        {
            ct.ThrowIfCancellationRequested();
            var hashed = sizeGroup
                .Select(f => (File: f, Hash: SourceHash(f)))
                .Where(x => x.Hash is not null)
                .GroupBy(x => x.Hash!.Value);
            foreach (var same in hashed)
            {
                // Primary = the least nested file (originals usually live above "backup" folders), then by path.
                var ordered = same.Select(x => x.File)
                    .OrderBy(f => f.Path.Count(c => c == '\\'))
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var dup in ordered.Skip(1))
                    result[dup] = new DuplicateInfo(DuplicateState.DuplicateInSource, ordered[0].Path);
            }
        }
        done = candidates.Count;
        progress?.Report((done, total));

        // 2. Duplicates already in the destination folder.
        if (!string.IsNullOrEmpty(targetRoot) && template is not null && Directory.Exists(targetRoot))
        {
            var folderIndex = new Dictionary<string, Dictionary<long, List<FileInfo>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (result[file].State == DuplicateState.New)
                {
                    // Exactly the folder the import would write to.
                    var folder = Importing.ImportPlan.DestinationFolder(targetRoot, template, file);
                    if (!folderIndex.TryGetValue(folder, out var bySize))
                        folderIndex[folder] = bySize = IndexFolder(folder);

                    if (bySize.TryGetValue(file.Size, out var sameSize) && SourceHash(file) is { } hash)
                    {
                        foreach (var existing in sameSize)
                        {
                            UInt128 existingHash;
                            try
                            {
                                existingHash = cache.GetOrCompute(existing, ct);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                continue;
                            }
                            if (existingHash == hash)
                            {
                                result[file] = new DuplicateInfo(DuplicateState.InTarget, existing.FullName);
                                break;
                            }
                        }
                    }
                }
                progress?.Report((++done, total));
            }

            // A source duplicate of a file that is already in the target is "already in target" too.
            var byPath = candidates.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                if (result[file] is { State: DuplicateState.DuplicateInSource, OtherPath: { } primaryPath })
                {
                    var primary = byPath[primaryPath];
                    if (result[primary] is { State: DuplicateState.InTarget } inTarget)
                        result[file] = inTarget;
                }
            }
        }

        progress?.Report((total, total));
        return result;
    }

    private static Dictionary<long, List<FileInfo>> IndexFolder(string folder)
    {
        var index = new Dictionary<long, List<FileInfo>>();
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) return index;
        try
        {
            foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                // A leftover temp file of an interrupted import is not an imported file.
                if (f.Name.EndsWith(".importing", StringComparison.OrdinalIgnoreCase)) continue;
                if (!index.TryGetValue(f.Length, out var list)) index[f.Length] = list = [];
                list.Add(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Treat unreadable folder as empty.
        }
        return index;
    }
}
