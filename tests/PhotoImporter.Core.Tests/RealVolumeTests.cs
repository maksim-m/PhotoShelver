using PhotoImporter.Core.Hashing;
using PhotoImporter.Core.Importing;

namespace PhotoImporter.Core.Tests;

/// <summary>
/// Runs only on a real second volume (an SD card, a USB disk, a virtual disk):
///   PHOTOIMPORTER_TEST_VOLUME           writable volume, e.g. "X:\" (a folder "PhotoImporterTest-*" is created and removed)
///   PHOTOIMPORTER_TEST_READONLY_VOLUME  read-only volume with the fixtures copied to "&lt;root&gt;\card"
/// Run:  dotnet test --filter Category=RealVolume
/// </summary>
[Trait("Category", "RealVolume")]
public class RealVolumeTests
{
    private static string? Writable => Environment.GetEnvironmentVariable("PHOTOIMPORTER_TEST_VOLUME");
    private static string? ReadOnly => Environment.GetEnvironmentVariable("PHOTOIMPORTER_TEST_READONLY_VOLUME");

    public sealed class WritableVolumeFact : FactAttribute
    {
        public WritableVolumeFact()
        {
            if (string.IsNullOrEmpty(Writable)) Skip = "PHOTOIMPORTER_TEST_VOLUME is not set";
        }
    }

    public sealed class ReadOnlyVolumeFact : FactAttribute
    {
        public ReadOnlyVolumeFact()
        {
            if (string.IsNullOrEmpty(ReadOnly)) Skip = "PHOTOIMPORTER_TEST_READONLY_VOLUME is not set";
        }
    }

    /// <summary>A scratch folder on the second volume, removed afterwards.</summary>
    private sealed class VolumeDir : IDisposable
    {
        public VolumeDir() => Directory.CreateDirectory(Path = System.IO.Path.Combine(Writable!, "PhotoImporterTest-" + Guid.NewGuid().ToString("N")[..8]));
        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [WritableVolumeFact]
    public void VolumeIsDetectedAsDifferent()
    {
        using var vol = new VolumeDir();
        using var local = new TempDir();
        Assert.False(PathUtil.IsSameVolume(vol.Path, local.Path));
        Assert.False(PathUtil.IsReadOnlyVolume(vol.Path));
        Assert.True(PathUtil.IsSameVolume(vol.Path, System.IO.Path.Combine(Writable!, "other")));
    }

    [WritableVolumeFact]
    public void UnbufferedReadWorksOnTheVolume()
    {
        using var vol = new VolumeDir();
        foreach (var size in new[] { 0, 1, 511, 4097, 3 * (1 << 20) + 7 })
        {
            var path = System.IO.Path.Combine(vol.Path, $"f{size}.bin");
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            Assert.Equal(new DiskHash(FileHasher.Hash(path), BypassedCache: true), FileHasher.HashFromDisk(path));
        }
    }

    [WritableVolumeFact]
    public async Task MoveFromTheVolumeLikeFromAnSdCard()
    {
        using var vol = new VolumeDir();
        using var local = new TempDir();
        var card = System.IO.Path.Combine(vol.Path, "card");
        var library = System.IO.Path.Combine(local.Path, "library");
        Fixtures.CopyTo(card);
        Directory.CreateDirectory(library);
        var before = new[] { Snapshot.Take(card), Snapshot.Take(library) };

        var outcome = await Pipeline.RunAsync(card, library, ImportMode.Move); // real cross-volume path, no test hooks

        Assert.Empty(outcome.Result.Failures);
        Assert.Empty(outcome.Result.Warnings);
        var after = new[] { Snapshot.Take(card), Snapshot.Take(library) };
        Assert.Equal(EndToEndTests.ExpectedTree, after[1].Paths);
        Assert.Equal([@"other\IMG_0001.XMP", @"other\notes.txt", @"photos\sub\IMG_0001_copy.JPG"], after[0].Paths);
        Invariants.NothingLost(before, after);
        Invariants.NoTempFiles(library);
    }

    [WritableVolumeFact]
    public async Task CopyOntoTheVolume()
    {
        using var vol = new VolumeDir();
        using var local = new TempDir();
        var source = System.IO.Path.Combine(local.Path, "card");
        var library = System.IO.Path.Combine(vol.Path, "library");
        Fixtures.CopyTo(source);
        Directory.CreateDirectory(library);
        var srcBefore = Snapshot.Take(source);

        var outcome = await Pipeline.RunAsync(source, library, ImportMode.Copy);

        Assert.Empty(outcome.Result.Failures);
        Assert.Empty(outcome.Result.Warnings);
        Assert.Equal(EndToEndTests.ExpectedTree, Snapshot.Take(library).Paths);
        Assert.Equal(srcBefore.Files, Snapshot.Take(source).Files);
        foreach (var imported in outcome.Result.Imported)
        {
            // exFAT stores times with 10 ms resolution (FAT32: 2 s).
            var delta = File.GetLastWriteTimeUtc(imported.DestinationPath) - File.GetLastWriteTimeUtc(imported.SourcePath);
            Assert.True(Math.Abs(delta.TotalSeconds) <= 2, $"{imported.DestinationPath}: time differs by {delta}");
        }
        Invariants.NoTempFiles(library);
    }

    [ReadOnlyVolumeFact]
    public async Task MoveFromWriteProtectedCardKeepsEverySource()
    {
        var card = Path.Combine(ReadOnly!, "card");
        using var local = new TempDir();
        var library = Path.Combine(local.Path, "library");
        Directory.CreateDirectory(library);
        var cardBefore = Snapshot.Take(card);

        // The app uses this to offer "copy instead of move" before starting.
        Assert.True(PathUtil.IsReadOnlyVolume(card));
        Assert.False(PathUtil.IsWritable(Directory.EnumerateFiles(card, "*.JPG", SearchOption.AllDirectories).First()));

        var outcome = await Pipeline.RunAsync(card, library, ImportMode.Move);

        // Everything is copied and verified, nothing on the card can be (or is) deleted.
        Assert.Equal(outcome.Jobs.Count, outcome.Result.Imported.Count);
        Assert.Equal(outcome.Jobs.Count, outcome.Result.Failures.Count);
        Assert.All(outcome.Result.Failures, f => Assert.Contains("не удалён", f.Message));
        Assert.Equal(cardBefore.Files, Snapshot.Take(card).Files);
        foreach (var imported in outcome.Result.Imported)
            Assert.Equal(cardBefore.Files[Path.GetRelativePath(card, imported.SourcePath)], FileHasher.Hash(imported.DestinationPath));
        Invariants.NoTempFiles(library);
    }
}
