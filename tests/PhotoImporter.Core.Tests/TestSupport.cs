using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Importing;
using PhotoImporter.Core.Metadata;
using PhotoImporter.Core.Scanning;

namespace PhotoImporter.Core.Tests;

internal static class Fixtures
{
    /// <summary>Fixed modification time for copied fixtures (git checkout times are arbitrary).</summary>
    public static readonly DateTime FixtureMtimeUtc = new(2025, 12, 31, 12, 0, 0, DateTimeKind.Utc);

    public static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static string MediaRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "media");

    /// <summary>Structural replicas of real device files (invented values).</summary>
    public static string FormatsRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "formats");

    /// <summary>Copies the fixture tree into <paramref name="destination"/> and normalizes timestamps.</summary>
    public static void CopyTo(string destination)
    {
        foreach (var file in Directory.EnumerateFiles(MediaRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(MediaRoot, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            File.SetLastWriteTimeUtc(target, FixtureMtimeUtc);
        }
    }
}

/// <summary>Content snapshot of a folder tree: relative path → hash.</summary>
internal sealed class Snapshot
{
    private Snapshot(string root, Dictionary<string, UInt128> files)
    {
        Root = root;
        Files = files;
    }

    public string Root { get; }
    public Dictionary<string, UInt128> Files { get; }

    public static Snapshot Take(string root)
    {
        var files = new Dictionary<string, UInt128>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(root))
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(root, f)] = FileHasher.Hash(f);
        }
        return new Snapshot(root, files);
    }

    public IEnumerable<string> Paths => Files.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
}

internal static class Invariants
{
    /// <summary>
    /// The core safety property: every piece of content that existed before (in source or target)
    /// still exists afterwards at least as many times.
    /// </summary>
    public static void NothingLost(IEnumerable<Snapshot> before, IEnumerable<Snapshot> after)
    {
        var countsBefore = before.SelectMany(s => s.Files.Values).GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
        var countsAfter = after.SelectMany(s => s.Files.Values).GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (hash, count) in countsBefore)
        {
            var now = countsAfter.GetValueOrDefault(hash);
            Assert.True(now >= count, $"Content {hash:X} existed {count}x before but {now}x after");
        }
    }

    /// <summary>Files that were in the target before must still be there, byte-identical.</summary>
    public static void TargetFilesUnchanged(Snapshot targetBefore, Snapshot targetAfter)
    {
        foreach (var (path, hash) in targetBefore.Files)
        {
            Assert.True(targetAfter.Files.TryGetValue(path, out var now), $"Pre-existing target file disappeared: {path}");
            Assert.True(now == hash, $"Pre-existing target file was modified: {path}");
        }
    }

    public static void NoTempFiles(string root)
    {
        if (!Directory.Exists(root)) return;
        Assert.Empty(Directory.GetFiles(root, "*.importing", SearchOption.AllDirectories));
    }
}

/// <summary>Runs the same pipeline as the app: enumerate → metadata → duplicates → import of "new" files.</summary>
internal static class Pipeline
{
    public sealed record Outcome(
        IReadOnlyList<SourceFile> Files,
        Dictionary<SourceFile, DuplicateInfo> Duplicates,
        IReadOnlyList<ImportJob> Jobs,
        ImportResult Result);

    public static async Task<Outcome> RunAsync(string source, string target, ImportMode mode,
        string template = "YYYY/MM-DD", Importer? importer = null)
    {
        var tpl = FolderTemplate.Parse(template);
        var enumerated = SourceScanner.Enumerate(source, recursive: true);
        await SourceScanner.ReadMetadataAsync(enumerated.Files, new CaptureDateReader(Fixtures.Berlin), null, default);
        var duplicates = DuplicateDetector.Detect(enumerated.Files, target, tpl, new HashCache(), null, default);
        // Same job planning as the app.
        var jobs = ImportPlan.CreateJobs(
            enumerated.Files.Where(f => f.Error is null && f.Date is not null && duplicates[f].State == DuplicateState.New), target, tpl);
        var result = (importer ?? new Importer()).Run(jobs, mode, null, default);
        return new Outcome(enumerated.Files, duplicates, jobs, result);
    }
}
