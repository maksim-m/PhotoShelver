namespace PhotoShelver.Core.Planning;

public sealed class FolderNode
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsNew { get; init; }

    /// <summary>Files that will be imported into this folder or any of its descendants.</summary>
    public int Count { get; set; }

    public List<FolderNode> Children { get; } = [];
}

public static class FolderTree
{
    /// <summary>
    /// Builds the preview tree of the target folder.
    /// </summary>
    /// <param name="relativeDestinations">Relative destination folder for every file that will be imported (one entry per file).</param>
    /// <param name="includeExisting">Also include existing target folders that receive no files (up to <paramref name="existingDepth"/> levels).</param>
    /// <param name="existing">
    /// The target's existing folders as listed by <see cref="ListExisting"/> (a caller may keep the listing for a few
    /// seconds instead of reading the disk on every change); null reads them now. Whether a destination folder is
    /// new is always checked on the disk.
    /// </param>
    public static List<FolderNode> Build(
        string targetRoot,
        IEnumerable<string> relativeDestinations,
        bool includeExisting,
        int existingDepth,
        CancellationToken ct = default,
        IReadOnlyList<string>? existing = null)
    {
        var root = new FolderNode { Name = "", FullPath = targetRoot };
        var existsCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool Exists(string path)
        {
            if (!existsCache.TryGetValue(path, out var e)) existsCache[path] = e = Directory.Exists(path);
            return e;
        }

        FolderNode GetOrAdd(FolderNode parent, string name, bool? knownExists = null)
        {
            var node = parent.Children.Find(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (node is not null) return node;
            var full = Path.Combine(parent.FullPath, name);
            node = new FolderNode { Name = name, FullPath = full, IsNew = !(knownExists ?? Exists(full)) };
            parent.Children.Add(node);
            return node;
        }

        foreach (var relative in relativeDestinations)
        {
            ct.ThrowIfCancellationRequested();
            var node = root;
            foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                node = GetOrAdd(node, segment);
                node.Count++;
            }
        }

        if (includeExisting && existingDepth > 0)
        {
            foreach (var relative in existing ?? ListExisting(targetRoot, existingDepth, ct))
            {
                ct.ThrowIfCancellationRequested();
                var node = root;
                foreach (var segment in relative.Split('\\'))
                    node = GetOrAdd(node, segment, true);
            }
        }

        Sort(root);
        return root.Children;
    }

    /// <summary>
    /// Existing folders of the target up to <paramref name="depth"/> levels, as relative paths ("2025", "2025\2025-08-14"),
    /// parents before their children. Hidden, system and inaccessible folders are left out. Empty when the target is missing.
    /// </summary>
    public static List<string> ListExisting(string targetRoot, int depth, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (depth > 0 && Directory.Exists(targetRoot)) AddExisting(targetRoot, "", depth, result, ct);
        return result;
    }

    private static void AddExisting(string folder, string relative, int depthLeft, List<string> result, CancellationToken ct)
    {
        List<DirectoryInfo> dirs;
        try
        {
            dirs = new DirectoryInfo(folder)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var path = relative.Length == 0 ? dir.Name : relative + "\\" + dir.Name;
            result.Add(path);
            if (depthLeft > 1) AddExisting(dir.FullName, path, depthLeft - 1, result, ct);
        }
    }

    private static void Sort(FolderNode node)
    {
        node.Children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var c in node.Children) Sort(c);
    }
}
