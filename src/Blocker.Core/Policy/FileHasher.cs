using System.IO.Abstractions;
using System.Security.Cryptography;

namespace Blocker.Core.Policy;

/// <summary>Streaming SHA-256 of executables with a bounded cache keyed by (path, length, last write time); any change invalidates the entry.</summary>
public class FileHasher(IFileSystem fileSystem, int maxCacheEntries = FileHasher.DefaultMaxCacheEntries)
{
    /// <summary>Default cache bound.</summary>
    public const int DefaultMaxCacheEntries = 4096;

    private readonly object _gate = new();
    private readonly int _max = maxCacheEntries > 0 ? maxCacheEntries : throw new ArgumentOutOfRangeException(nameof(maxCacheEntries));
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _order = new();

    private readonly record struct CacheEntry(long Length, DateTime LastWriteUtc, string Hash);

    /// <summary>Number of cached hashes.</summary>
    public int CacheCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>Lower-case hex SHA-256 of <paramref name="path"/>, or <see langword="null"/> when the file is missing or unreadable (fail closed).</summary>
    public ValueTask<string?> GetSha256Async(string path, CancellationToken cancellationToken = default) => GetSha256Async(path, long.MaxValue, cancellationToken);

    /// <summary>
    /// Lower-case hex SHA-256 of <paramref name="path"/>, or <see langword="null"/> when the file is missing, unreadable or larger than
    /// <paramref name="maxBytes"/> (it is then not opened at all). Virtual so tests can stand in for a slow disk.
    /// </summary>
    /// <param name="path">File to hash.</param>
    /// <param name="maxBytes">Largest file that is read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The hash, or <see langword="null"/>.</returns>
    public virtual async ValueTask<string?> GetSha256Async(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            IFileInfo info = fileSystem.FileInfo.New(path);
            if (!info.Exists || info.Length > maxBytes)
            {
                return null;
            }

            (long length, DateTime written) = (info.Length, info.LastWriteTimeUtc);
            lock (_gate)
            {
                if (_cache.TryGetValue(path, out CacheEntry hit) && hit.Length == length && hit.LastWriteUtc == written)
                {
                    return hit.Hash;
                }
            }

            string hash;
            await using (Stream stream = fileSystem.File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            }

            info.Refresh();
            if (info.Length == length && info.LastWriteTimeUtc == written)
            {
                Store(path, new CacheEntry(length, written, hash));
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Store(string path, CacheEntry entry)
    {
        lock (_gate)
        {
            if (!_cache.ContainsKey(path))
            {
                while (_cache.Count >= _max && _order.Count > 0)
                {
                    _cache.Remove(_order.Dequeue());
                }

                _order.Enqueue(path);
            }

            _cache[path] = entry;
        }
    }
}
