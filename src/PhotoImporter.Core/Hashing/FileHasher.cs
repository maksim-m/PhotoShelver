using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoImporter.Core.Hashing;

/// <param name="BypassedCache">True when the data was read from the device itself, not from the Windows cache.</param>
public readonly record struct DiskHash(UInt128 Hash, bool BypassedCache);

public static class FileHasher
{
    /// <summary>
    /// Chunk for every read, copy and hash: 4 MB (a multiple of every sector size, as unbuffered reads require).
    /// Measured on NVMe: unbuffered reads run at ~2.3 GB/s with 4 MB chunks vs ~1.0–1.4 GB/s with 1 MB.
    /// </summary>
    internal const int BufferSize = 4 * 1024 * 1024;

    public static UInt128 Hash(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return Hash(fs, ct);
    }

    /// <summary>
    /// Hashes the file as stored on the device, bypassing the Windows file cache (FILE_FLAG_NO_BUFFERING),
    /// so a freshly written copy is really read back from disk and a source is really re-read from the card.
    /// When the file system does not allow unbuffered access the file is read normally and
    /// <see cref="DiskHash.BypassedCache"/> is false (the check is then weaker and the caller should say so).
    /// </summary>
    public static unsafe DiskHash HashFromDisk(string path, CancellationToken ct = default, bool forceCached = false)
    {
        const FileOptions noBuffering = (FileOptions)0x20000000;
        const int chunk = BufferSize;
        if (forceCached) return new DiskHash(Hash(path, ct), BypassedCache: false);

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, noBuffering | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Missing files and files locked by someone else are real errors, not "unbuffered I/O unsupported".
            if (ex is FileNotFoundException or DirectoryNotFoundException || ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)) throw;
            return new DiskHash(Hash(path, ct), BypassedCache: false);
        }

        using (handle)
        {
            // Unbuffered I/O needs sector-aligned memory, offsets and sizes.
            var memory = NativeMemory.AlignedAlloc(chunk, 4096);
            try
            {
                var buffer = new Span<byte>(memory, chunk);
                var hasher = new XxHash128();
                long offset = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = RandomAccess.Read(handle, buffer, offset);
                    if (read <= 0) break;
                    hasher.Append(buffer[..read]);
                    offset += read;
                    if (read < chunk) break; // end of file
                }
                return new DiskHash(hasher.GetCurrentHashAsUInt128(), BypassedCache: true);
            }
            finally
            {
                NativeMemory.AlignedFree(memory);
            }
        }
    }

    public static UInt128 Hash(Stream stream, CancellationToken ct = default)
    {
        var hasher = new XxHash128();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.Append(buffer.AsSpan(0, read));
            }
            return hasher.GetCurrentHashAsUInt128();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Thread-safe in-memory cache of file hashes keyed by path + size + modification time.</summary>
public sealed class HashCache
{
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), UInt128> _cache = new();

    public UInt128 GetOrCompute(string path, long size, DateTime lastWriteUtc, CancellationToken ct = default)
    {
        var key = (Path.GetFullPath(path).ToUpperInvariant(), size, lastWriteUtc.Ticks);
        if (_cache.TryGetValue(key, out var hash)) return hash;
        hash = FileHasher.Hash(path, ct);
        _cache[key] = hash;
        return hash;
    }

    public UInt128 GetOrCompute(FileInfo file, CancellationToken ct = default) =>
        GetOrCompute(file.FullName, file.Length, file.LastWriteTimeUtc, ct);

    public void Store(string path, long size, DateTime lastWriteUtc, UInt128 hash) =>
        _cache[(Path.GetFullPath(path).ToUpperInvariant(), size, lastWriteUtc.Ticks)] = hash;
}
