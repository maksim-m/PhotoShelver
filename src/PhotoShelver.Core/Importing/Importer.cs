using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using PhotoShelver.Core.Hashing;

namespace PhotoShelver.Core.Importing;

public enum ImportMode
{
    Move,
    Copy,
}

/// <param name="DestinationName">Name of the file in the destination folder (null: the source's own name).</param>
/// <param name="BackupFolder">Folder of the second copy (null: no second copy).</param>
/// <param name="BackupName">Name of the second copy (null: the source's own name).</param>
/// <param name="BackupReusesIdentical">
/// A file with identical content already in <paramref name="BackupFolder"/> counts as the second copy (mirror layout:
/// a file imported again after it was removed from the library is not copied there a second time).
/// </param>
public sealed record ImportJob(string SourcePath, string DestinationFolder, long Size, string? DestinationName = null,
    string? BackupFolder = null, string? BackupName = null, bool BackupReusesIdentical = false)
{
    /// <summary>The name the file gets in the destination folder (before a _1, _2… suffix on a clash).</summary>
    public string FileName => DestinationName ?? Path.GetFileName(SourcePath);

    /// <summary>The name of the second copy (before a suffix on a clash).</summary>
    public string BackupFileName => BackupName ?? Path.GetFileName(SourcePath);

    /// <summary>Bytes written for this job: one copy, or two with a second copy.</summary>
    public long BytesToWrite => BackupFolder is null ? Size : 2 * Size;
}

public sealed record ImportProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string? CurrentFile);

public sealed record ImportFailure(string SourcePath, string Message);

/// <param name="Hash">xxHash128 of the verified content (null for a same-volume rename, which does not read the file).</param>
/// <param name="BackupPath">The verified second copy, or null when there is none.</param>
public sealed record ImportedFile(string SourcePath, string DestinationPath, UInt128? Hash = null, string? BackupPath = null);

/// <param name="Warnings">Non-fatal remarks about the import as a whole (e.g. a weaker verification on some volume).</param>
public sealed record ImportResult(
    IReadOnlyList<ImportedFile> Imported,
    IReadOnlyList<ImportFailure> Failures,
    bool Cancelled,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Copies or moves files into the target, never overwriting anything (SPEC §7).
/// </summary>
public sealed class Importer
{
    private const int MaxSuffix = 100_000;
    private const int TempNameAttempts = 5;
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

    /// <summary>Test hook: pretend the file system does not support reading around the cache.</summary>
    internal bool ForceCachedVerification { get; init; }

    /// <summary>
    /// Commits a placed file (its name included) to the disk before the source is deleted. Test hook: replaceable.
    /// FlushFileBuffers on a file makes NTFS flush its log (which holds the rename) and FAT/exFAT write the file's
    /// directory entry. The file is opened for writing only to be allowed to flush; nothing is written.
    /// </summary>
    internal Action<string> CommitPlaced { get; init; } = path =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
        stream.Flush(flushToDisk: true);
    };

    /// <summary>Waits between retries of operations blocked by another process (tests use short ones).</summary>
    internal IReadOnlyList<TimeSpan> RetryDelays { get; init; } = TransientRetry.DefaultDelays;

    public ImportResult Run(
        IReadOnlyList<ImportJob> jobs,
        ImportMode mode,
        IProgress<ImportProgress>? progress,
        CancellationToken ct,
        Action<ImportFileOutcome>? onFileDone = null)
    {
        var imported = new List<ImportedFile>();
        var failures = new List<ImportFailure>();
        var cachedVolumes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var bytesTotal = jobs.Sum(j => j.BytesToWrite);
        long bytesDone = 0;
        var clock = Stopwatch.StartNew();
        var lastReport = TimeSpan.MinValue;

        void Report(int filesDone, long bytes, string? current, bool force = false)
        {
            if (!force && clock.Elapsed - lastReport < ProgressInterval) return;
            lastReport = clock.Elapsed;
            progress?.Report(new ImportProgress(filesDone, jobs.Count, bytes, bytesTotal, current));
        }

        ImportResult Result(bool cancelled) => new(imported, failures, cancelled, [.. Warnings(cachedVolumes), .. warnings]);

        for (var i = 0; i < jobs.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return Result(cancelled: true);

            var job = jobs[i];
            var startBytes = bytesDone;
            var importedBefore = imported.Count;
            var failuresBefore = failures.Count;
            Report(i, bytesDone, job.SourcePath, force: true);
            try
            {
                ImportOne(job, mode, b => Report(i, startBytes + b, job.SourcePath), imported, failures, cachedVolumes, warnings);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                // Any per-file problem is reported; the source is never deleted on this path.
                failures.Add(new ImportFailure(job.SourcePath, ex.Message));
            }
            bytesDone = startBytes + job.BytesToWrite;
            onFileDone?.Invoke(new ImportFileOutcome(
                job,
                imported.Count > importedBefore ? imported[^1] : null,
                failures.Count > failuresBefore ? failures[^1].Message : null));
        }

        Report(jobs.Count, bytesDone, null, force: true);
        return Result(cancelled: false);
    }

    private static List<string> Warnings(IEnumerable<string> cachedVolumes) =>
        cachedVolumes
            .Select(v => L.T(
                $"Drive “{v}” does not support reading around the Windows cache: copies from and to it are verified less strictly. When moving, sources on it are not deleted.",
                $"Носитель «{v}» не поддерживает чтение в обход кэша Windows: проверка копий с него и на него слабее обычной. При перемещении исходники с такого носителя не удаляются."))
            .ToList();

    private void ImportOne(ImportJob job, ImportMode mode, Action<long> onBytes,
        List<ImportedFile> imported, List<ImportFailure> failures, ISet<string> cachedVolumes, List<string> warnings)
    {
        var source = new FileInfo(job.SourcePath);
        if (!source.Exists)
            throw new FileNotFoundException(L.T("Source file not found", "Исходный файл не найден"), job.SourcePath);
        if (SamePath(source.DirectoryName!, job.DestinationFolder))
            throw new IOException(L.T("The file is already in the destination folder", "Файл уже находится в папке назначения"));
        // Defence in depth: a validated name template can never produce a path or an invalid name.
        if (!IsPlainFileName(job.FileName))
            throw new IOException(L.T($"Invalid file name “{job.FileName}”", $"Недопустимое имя файла «{job.FileName}»"));
        if (job.BackupFolder is not null)
        {
            if (!IsPlainFileName(job.BackupFileName))
                throw new IOException(L.T($"Invalid file name “{job.BackupFileName}”", $"Недопустимое имя файла «{job.BackupFileName}»"));
            if (SamePath(source.DirectoryName!, job.BackupFolder) || SamePath(job.DestinationFolder, job.BackupFolder))
                throw new IOException(L.T("The second copy would be in the same folder as the source or the imported file",
                    "Вторая копия оказалась бы в одной папке с исходным или импортированным файлом"));
        }

        // Snapshot to detect changes of the source while it is being copied.
        var sizeBefore = source.Length;
        var writeTimeBefore = source.LastWriteTimeUtc;

        // The second copy is made first, from the source: a move can then still be an atomic rename. If it fails,
        // the file is imported all the same, but the source stays where it is (a move never leaves fewer than two copies).
        Backup? backup = null;
        string? backupError = null;
        if (job.BackupFolder is not null)
        {
            try
            {
                backup = PlaceBackup(job, onBytes, cachedVolumes);
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                backupError = ex.Message;
            }
        }
        var backupBytes = job.BackupFolder is null ? 0 : job.Size;

        string dest;
        bool sourceReadFromDevice;
        UInt128 hash;
        try
        {
            Directory.CreateDirectory(job.DestinationFolder);

            if (mode == ImportMode.Move && backupError is null && !TreatAsDifferentVolumes && PathUtil.IsSameVolume(job.SourcePath, job.DestinationFolder))
            {
                // The new second copy is committed to its disk before the source is renamed away.
                if (backup is { IsNew: true })
                    TransientRetry.Run(() => CommitPlaced(backup.Path), RetryDelays);
                // Atomic rename: the file is either still at the source or already at the destination.
                var moved = MoveSameVolume(job);
                imported.Add(new ImportedFile(job.SourcePath, moved, null, backup?.Path));
                // A renamed file keeps its attributes; files in the library are always writable (copies are created writable).
                ClearReadOnly(moved, warnings);
                return;
            }

            (dest, sourceReadFromDevice, hash) = CopyViaTemp(job.SourcePath, job.DestinationFolder, job.FileName,
                b => onBytes(backupBytes + b), cachedVolumes);
        }
        catch (Exception ex) when (!IsFatal(ex) && backup is not null)
        {
            // Nothing in the library (the source is where it was): say where the second copy is.
            failures.Add(new ImportFailure(job.SourcePath, ex.Message + L.T($" (the second copy is in “{backup.Path}”)", $" (вторая копия — «{backup.Path}»)")));
            return;
        }
        imported.Add(new ImportedFile(job.SourcePath, dest, hash, backup?.Path));

        if (backupError is not null)
        {
            failures.Add(new ImportFailure(job.SourcePath, mode == ImportMode.Move
                ? L.T($"Copied to “{dest}”, but the second copy was not made, so the source was not deleted: {backupError}",
                    $"Скопирован в «{dest}», но вторая копия не создана, поэтому исходный файл не удалён: {backupError}")
                : L.T($"Copied to “{dest}”, but the second copy was not made: {backupError}",
                    $"Скопирован в «{dest}», но вторая копия не создана: {backupError}")));
            return;
        }
        if (backup is not null && backup.Hash != hash)
        {
            // Each copy was verified against the source at its own time: the source changed in between.
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”, but the source changed between the two copies; the source was not deleted",
                $"Скопирован в «{dest}», но исходный файл изменился между двумя копиями; исходный файл не удалён")));
            return;
        }
        if (mode != ImportMode.Move) return;

        // Cross-volume move: delete the source only if it is exactly what was copied and verified.
        source.Refresh();
        if (!source.Exists) return;
        if (source.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            // Protected in the camera (or by the user): never remove that protection to delete the original.
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”; the source is protected (read-only attribute, e.g. protected in the camera) and was not deleted",
                $"Скопирован в «{dest}»; исходный файл защищён (атрибут «только чтение», например защита в камере) и не удалён")));
            return;
        }
        if (!sourceReadFromDevice)
        {
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”, but the source was not deleted: its drive does not allow a reliable re-read for verification",
                $"Скопирован в «{dest}», но исходный файл не удалён: носитель не позволяет надёжно перечитать его для проверки")));
            return;
        }
        if (source.Length != sizeBefore || source.LastWriteTimeUtc != writeTimeBefore)
        {
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”, but the source changed while it was copied and was not deleted",
                $"Скопирован в «{dest}», но исходный файл изменился во время копирования и не удалён")));
            return;
        }
        try
        {
            // The rename "temp → final name" may still sit in the target's cache, while deleting from a card is written
            // at once: after a power cut or a crash the target could hold only the temp file and the card nothing.
            // So the placed file (and a new second copy) is committed to the disk first.
            TransientRetry.Run(() => CommitPlaced(dest), RetryDelays);
            if (backup is { IsNew: true })
                TransientRetry.Run(() => CommitPlaced(backup.Path), RetryDelays);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”, but writing to the disk could not be completed; the source was not deleted: {ex.Message}",
                $"Скопирован в «{dest}», но не удалось завершить запись на диск, исходный файл не удалён: {ex.Message}")));
            return;
        }
        try
        {
            TransientRetry.Run(() => File.Delete(job.SourcePath), RetryDelays);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            failures.Add(new ImportFailure(job.SourcePath, L.T($"Copied to “{dest}”, but the source was not deleted: {ex.Message}",
                $"Скопирован в «{dest}», но исходный файл не удалён: {ex.Message}")));
        }
    }

    /// <param name="IsNew">False when an identical file already in the folder counts as the second copy (mirror layout).</param>
    private sealed record Backup(string Path, UInt128 Hash, bool IsNew);

    /// <summary>Makes the verified second copy of the source (or finds an identical one, when the job allows it).</summary>
    private Backup PlaceBackup(ImportJob job, Action<long> onBytes, ISet<string> cachedVolumes)
    {
        Directory.CreateDirectory(job.BackupFolder!);
        if (job.BackupReusesIdentical && FindIdentical(job.SourcePath, job.Size, job.BackupFolder!) is { } same)
            return same;
        var (path, _, hash) = CopyViaTemp(job.SourcePath, job.BackupFolder!, job.BackupFileName, onBytes, cachedVolumes);
        return new Backup(path, hash, IsNew: true);
    }

    /// <summary>A file directly in <paramref name="folder"/> with the same content as the source, both read from their devices.</summary>
    private Backup? FindIdentical(string sourcePath, long size, string folder)
    {
        var candidates = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => f.Length == size && !ImportLeftovers.IsLeftover(f.Name))
            .ToList();
        if (candidates.Count == 0) return null;
        var sourceHash = FileHasher.HashFromDisk(sourcePath, forceCached: ForceCachedVerification).Hash;
        foreach (var candidate in candidates)
        {
            if (FileHasher.HashFromDisk(candidate.FullName, forceCached: ForceCachedVerification).Hash == sourceHash)
                return new Backup(candidate.FullName, sourceHash, IsNew: false);
        }
        return null;
    }

    private static void ClearReadOnly(string path, List<string> warnings)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            warnings.Add(L.T($"Could not clear the read-only attribute of “{path}”: {ex.Message}", $"Не удалось снять атрибут «только чтение» с «{path}»: {ex.Message}"));
        }
    }

    private static bool IsFatal(Exception ex) => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && name.Trim() == name && !name.EndsWith('.') && name is not ("." or "..")
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !FolderTemplate.ReservedNames.Contains(name.Split('.')[0]); // CON, NUL.JPG… would address a device

    private static bool SamePath(string a, string b) =>
        string.Equals(PathUtil.WithoutTrailingSeparator(a), PathUtil.WithoutTrailingSeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Rename within the same volume; tries name, name_1, name_2… without overwriting.</summary>
    private string MoveSameVolume(ImportJob job)
    {
        return PlaceWithSuffix(job.FileName, job.DestinationFolder, candidate => File.Move(job.SourcePath, candidate, overwrite: false));
    }

    /// <summary>
    /// Copies to a temporary "&lt;name&gt;.&lt;8 hex&gt;.importing" file, verifies it, then renames it to a free name.
    /// Verification (both modes): the copy is read back from disk bypassing the cache, and the source is
    /// re-read from its device; both must match the data that was copied. On any mismatch nothing is placed.
    /// Returns the placed path and whether the source re-read really came from the device.
    /// </summary>
    private (string Path, bool SourceReadFromDevice, UInt128 Hash) CopyViaTemp(string sourcePath, string folder, string fileName,
        Action<long> onBytes, ISet<string> cachedVolumes)
    {
        string? temp = null;
        var ownsTemp = false; // only ever delete a temp file this call created
        try
        {
            UInt128 copiedHash = default;
            for (var attempt = 1; ; attempt++)
            {
                temp = Path.Combine(folder, TempFileName?.Invoke(fileName) ?? $"{fileName}.{Random.Shared.Next():x8}.importing");
                try
                {
                    copiedHash = CopyAndHash(sourcePath, temp, onBytes, () => ownsTemp = true);
                    break;
                }
                catch (IOException) when (!ownsTemp && File.Exists(temp) && attempt < TempNameAttempts)
                {
                    // The random temp name is taken (e.g. by a leftover): pick another one, never touch the existing file.
                }
            }
            AfterTempWritten?.Invoke(temp);

            var (copyOnDisk, sourceOnDevice) = ReadBothFromDevice(temp, sourcePath);
            if (copyOnDisk.Hash != copiedHash)
                throw new InvalidDataException(L.T("The copy on the disk does not match the data that was read (write error). The file was not imported",
                    "Копия на диске не совпадает с прочитанными данными (ошибка записи). Файл не импортирован"));
            if (sourceOnDevice.Hash != copiedHash)
                throw new InvalidDataException(L.T("The source reads differently from what was copied (unstable reading or the file changed). The file was not imported",
                    "Исходный файл при повторном чтении отличается от скопированного (нестабильное чтение или файл изменился). Файл не импортирован"));
            if (!copyOnDisk.BypassedCache) cachedVolumes.Add(VolumeLabel(folder));
            if (!sourceOnDevice.BypassedCache) cachedVolumes.Add(VolumeLabel(sourcePath));

            var src = new FileInfo(sourcePath);
            try
            {
                File.SetCreationTimeUtc(temp, src.CreationTimeUtc);
                File.SetLastWriteTimeUtc(temp, src.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Timestamps are nice to have; some file systems reject them.
            }

            var tempPath = temp;
            var placed = PlaceWithSuffix(fileName, folder, candidate => File.Move(tempPath, candidate, overwrite: false));
            ownsTemp = false; // it has become the imported file
            return (placed, sourceOnDevice.BypassedCache, copiedHash);
        }
        finally
        {
            if (ownsTemp && temp is not null)
            {
                try
                {
                    var tempPath = temp;
                    TransientRetry.Run(() => File.Delete(tempPath), RetryDelays);
                }
                catch (Exception ex) when (!IsFatal(ex))
                {
                    // Best effort cleanup; a leftover is reported by the app and ignored by the duplicate check.
                }
            }
        }
    }

    /// <summary>
    /// Re-reads the copy and the source from their devices. On different volumes both reads run at the same time
    /// (each device works at its own speed); on one volume they run one after the other, so a hard disk does not
    /// seek back and forth. Either way both results are complete before anything is decided, and an error of
    /// either read is thrown (the copy's first, as before).
    /// </summary>
    private (DiskHash Copy, DiskHash Source) ReadBothFromDevice(string copy, string source)
    {
        if (!TreatAsDifferentVolumes && PathUtil.IsSameVolume(copy, source))
            return (FileHasher.HashFromDisk(copy, forceCached: ForceCachedVerification),
                    FileHasher.HashFromDisk(source, forceCached: ForceCachedVerification));

        var sourceRead = Task.Run(() => FileHasher.HashFromDisk(source, forceCached: ForceCachedVerification));
        DiskHash copyHash;
        try
        {
            copyHash = FileHasher.HashFromDisk(copy, forceCached: ForceCachedVerification);
        }
        catch
        {
            // Never leave the source read running behind our back.
            try { sourceRead.Wait(); } catch (AggregateException) { }
            throw;
        }
        return (copyHash, sourceRead.GetAwaiter().GetResult()); // GetResult rethrows the original exception
    }

    private static string VolumeLabel(string path) => Path.GetPathRoot(Path.GetFullPath(path)) ?? path;

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
    /// A placement blocked by another process (antivirus, indexer) is retried on the same name.
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
                TransientRetry.Run(() => place(candidate), RetryDelays);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
            {
                // Lost a race with another writer: try the next suffix.
            }
        }
        throw new IOException(L.T($"Could not find a free name for “{fileName}”", $"Не удалось подобрать свободное имя для «{fileName}»"));
    }
}
