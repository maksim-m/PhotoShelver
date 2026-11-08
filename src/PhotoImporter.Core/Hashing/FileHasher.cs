using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PhotoImporter.Core.Hashing;

public static class FileHasher
{
    internal const int BufferSize = 1024 * 1024;

    public static UInt128 Hash(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return Hash(fs, ct);
    }

    /// <summary>
    /// Hashes the file as stored on the device, bypassing the Windows file cache (FILE_FLAG_NO_BUFFERING),
    /// so a freshly written copy is really read back from disk and a source is really re-read from the card.
    /// Falls back to a normal read when the file system does not allow unbuffered access.
    /// </summary>
    public static unsafe UInt128 HashFromDisk(string path, CancellationToken ct = default)
    {
        const FileOptions noBuffering = (FileOptions)0x20000000;
        const int chunk = 1 << 20; // a multiple of every sector size
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, noBuffering | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException) throw;
            return Hash(path, ct);
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
                return hasher.GetCurrentHashAsUInt128();
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
