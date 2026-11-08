using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;

namespace PhotoImporter.Core.Hashing;

public static class FileHasher
{
    internal const int BufferSize = 1024 * 1024;

    public static UInt128 Hash(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        return Hash(fs, ct);
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
