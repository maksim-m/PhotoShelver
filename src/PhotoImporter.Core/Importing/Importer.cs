using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using PhotoImporter.Core.Hashing;

namespace PhotoImporter.Core.Importing;

public enum ImportMode
{
    Move,
    Copy,
}

public sealed record ImportJob(string SourcePath, string DestinationFolder, long Size);

public sealed record ImportProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? CurrentFile);

public sealed record ImportFailure(string SourcePath, string Message);

public sealed record ImportedFile(string SourcePath, string DestinationPath);

public sealed record ImportResult(
    IReadOnlyList<ImportedFile> Imported,
    IReadOnlyList<ImportFailure> Failures,
    bool Cancelled);

/// <summary>
/// Copies or moves files into the target, never overwriting anything (SPEC §7).
/// </summary>
public sealed class Importer
{
    private const int MaxSuffix = 100_000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Test hook: handle moves as cross-volume (copy + verify + delete).</summary>
    internal bool TreatAsDifferentVolumes { get; init; }

    /// <summary>Test hook: called with the temp file path after copying, before verification.</summary>
    internal Action<string>? AfterTempWritten { get; init; }

    /// <summary>Test hook: called after a free name was found and right before the file is placed there (race window).</summary>
    internal Action<string>? BeforePlace { get; init; }

    /// <summary>Test hook: called after each chunk is written (throw to simulate a full disk or a removed card).</summary>
    internal Action<long>? DuringCopy { get; init; }

    /// <summary>Test hook: overrides the temp file name (file name → temp name).</summary>
    internal Func<string, string>? TempFileName { get; init; }

    public ImportResult Run(
        IReadOnlyList<ImportJob> jobs,
        ImportMode mode,
        IProgress<ImportProgress>? progress,
        CancellationToken ct)
    {
        var imported = new List<ImportedFile>();
        var failures = new List<ImportFailure>();
        var bytesTotal = jobs.Sum(j => j.Size);
        long bytesDone = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.MinValue;

        void Report(int filesDone, long bytes, string? current, bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < ProgressInterval) return;
            lastReport = clock.Elapsed;
            progress?.Report(new ImportProgress(filesDone, jobs.Count, bytes, bytesTotal, current));
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new ImportResult(imported, failures, true);

            var job = jobs[i];
            var startBytes = bytesDone;
            Report(i, bytesDone, job.SourcePath, force: true);
            try
            {
                ImportOne(job, mode, b => Report(i, startBytes + b, job.SourcePath), imported, failures);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                // Any per-file problem is reported; the source is never deleted on this path.
                failures.Add(new ImportFailure(job.SourcePath, ex.Message));
            }
            bytesDone = startBytes + job.Size;
        }

        Report(jobs.Count, bytesDone, null, force: true);
        return new ImportResult(imported, failures, false);
    }

    private void ImportOne(ImportJob job, ImportMode mode, Action<long> onBytes, List<ImportedFile> imported, List<ImportFailure> failures)
    {
        var source = new FileInfo(job.SourcePath);
        if (!source.Exists)
            throw new FileNotFoundException("Исходный файл не найден", job.SourcePath);
        if (SamePath(source.DirectoryName!, job.DestinationFolder))
            throw new IOException("Файл уже находится в папке назначения");

        // Snapshot to detect changes of the source while it is being copied.
        var sizeBefore = source.Length;
        var writeTimeBefore = source.LastWriteTimeUtc;

        Directory.CreateDirectory(job.DestinationFolder);

        if (mode == ImportMode.Move && !TreatAsDifferentVolumes && PathUtil.IsSameVolume(job.SourcePath, job.DestinationFolder))
        {
            // Atomic rename: the file is either still at the source or already at the destination.
            imported.Add(new ImportedFile(job.SourcePath, MoveSameVolume(job)));
            return;
        }

        var dest = CopyViaTemp(job, onBytes);
        imported.Add(new ImportedFile(job.SourcePath, dest));
        if (mode != ImportMode.Move) return;

        // Cross-volume move: delete the source only if it is exactly what was copied and verified.
        source.Refresh();
        if (!source.Exists) return;
        if (source.Length != sizeBefore || source.LastWriteTimeUtc != writeTimeBefore)
        {
            failures.Add(new ImportFailure(job.SourcePath, $"Скопирован в «{dest}», но исходный файл изменился во время копирования и не удалён"));
            return;
        }
        try
        {
            File.Delete(job.SourcePath);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            failures.Add(new ImportFailure(job.SourcePath, $"Скопирован в «{dest}», но исходный файл не удалён: {ex.Message}"));
        }
    }

    private static bool IsFatal(Exception ex) => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Rename within the same volume; tries name, name_1, name_2… without overwriting.</summary>
    private string MoveSameVolume(ImportJob job)
    {
        var fileName = Path.GetFileName(job.SourcePath);
        return PlaceWithSuffix(fileName, job.DestinationFolder, candidate => File.Move(job.SourcePath, candidate, overwrite: false));
    }

    /// <summary>
    /// Copies to a temporary *.importing file, verifies it, then renames it to a free name.
    /// Verification (both modes): the copy is read back from disk bypassing the cache, and the source is
    /// re-read from its device; both must match the data that was copied. On any mismatch nothing is placed.
    /// </summary>
    private string CopyViaTemp(ImportJob job, Action<long> onBytes)
    {
        var fileName = Path.GetFileName(job.SourcePath);
        var temp = Path.Combine(job.DestinationFolder, TempFileName?.Invoke(fileName) ?? $"{fileName}.{Guid.NewGuid():N}.importing");
        var ownsTemp = false; // only ever delete a temp file this call created
        try
        {
            var copiedHash = CopyAndHash(job.SourcePath, temp, onBytes, () => ownsTemp = true);
            AfterTempWritten?.Invoke(temp);

            if (FileHasher.HashFromDisk(temp) != copiedHash)
                throw new InvalidDataException("Копия на диске не совпадает с прочитанными данными (ошибка записи). Файл не импортирован");
            if (FileHasher.HashFromDisk(job.SourcePath) != copiedHash)
                throw new InvalidDataException("Исходный файл при повторном чтении отличается от скопированного (нестабильное чтение или файл изменился). Файл не импортирован");

            var src = new FileInfo(job.SourcePath);
            try
            {
                File.SetCreationTimeUtc(temp, src.CreationTimeUtc);
                File.SetLastWriteTimeUtc(temp, src.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Timestamps are nice to have; some file systems reject them.
            }

            var placed = PlaceWithSuffix(fileName, job.DestinationFolder, candidate => File.Move(temp, candidate, overwrite: false));
            ownsTemp = false; // it has become the imported file
            return placed;
        }
        finally
        {
            if (ownsTemp)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (!IsFatal(ex))
                {
                    // Best effort cleanup; a leftover *.importing file is ignored by the duplicate check.
                }
            }
        }
    }

    private UInt128 CopyAndHash(string source, string destination, Action<long> onBytes, Action created)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(FileHasher.BufferSize);
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan);
            created();
            output.SetLength(input.Length);
            long total = 0;
            int read;
            while ((read = input.Read(buffer, 0, FileHasher.BufferSize)) > 0)
            {
                hasher.Append(buffer.AsSpan(0, read));
                output.Write(buffer, 0, read);
                total += read;
                DuringCopy?.Invoke(total);
                onBytes(total);
            }
            output.SetLength(total);
            output.Flush(flushToDisk: true);
            return hasher.GetCurrentHashAsUInt128();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Calls <paramref name="place"/> with name, name_1, name_2… until it succeeds.
    /// <paramref name="place"/> must fail (IOException) when the destination already exists.
    /// </summary>
    private string PlaceWithSuffix(string fileName, string folder, Action<string> place)
    {
        for (var n = 0; n < MaxSuffix; n++)
        {
            var candidate = Path.Combine(folder, PathUtil.WithSuffix(fileName, n));
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                BeforePlace?.Invoke(candidate);
                place(candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Lost a race with another writer: try the next suffix.
            }
        }
        throw new IOException($"Не удалось подобрать свободное имя для «{fileName}»");
    }
}
