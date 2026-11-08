namespace PhotoImporter.Core.Planning;

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
    public static List<FolderNode> Build(
        string targetRoot,
        IEnumerable<string> relativeDestinations,
        bool includeExisting,
        int existingDepth,
        CancellationToken ct = default)
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

        if (includeExisting && existingDepth > 0 && Directory.Exists(targetRoot))
            AddExisting(root, existingDepth, GetOrAdd, ct);

        Sort(root);
        return root.Children;
    }

    private static void AddExisting(FolderNode node, int depthLeft, Func<FolderNode, string, bool?, FolderNode> getOrAdd, CancellationToken ct)
    {
        IEnumerable<DirectoryInfo> dirs;
        try
        {
            dirs = new DirectoryInfo(node.FullPath)
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
            var child = getOrAdd(node, dir.Name, true);
            if (depthLeft > 1) AddExisting(child, depthLeft - 1, getOrAdd, ct);
        }
    }

    private static void Sort(FolderNode node)
    {
        node.Children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var c in node.Children) Sort(c);
    }
}
