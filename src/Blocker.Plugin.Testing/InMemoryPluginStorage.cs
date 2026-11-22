using Blocker.Sdk.Storage;

namespace Blocker.Plugin.Testing;

/// <summary>
/// In-memory <see cref="IPluginStorage"/> enforcing the production rules: keys are validated with
/// <see cref="PluginStorageKey.ThrowIfInvalid"/>, values with unpaired UTF-16 surrogates are rejected with
/// <see cref="ArgumentException"/> and the quota counts the UTF-8 size of the JSON-escaped key and value per entry.
/// </summary>
public sealed class InMemoryPluginStorage : IPluginStorage
{
    /// <summary>Default quota: 1 MiB.</summary>
    public const long DefaultQuotaBytes = 1024 * 1024;

    private readonly SortedDictionary<string, string> _items = new(StringComparer.Ordinal);
    private long _usedBytes;

    /// <summary>Creates storage with the given quota.</summary>
    /// <param name="quotaBytes">Maximum total size of all keys and values, in bytes.</param>
    public InMemoryPluginStorage(long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quotaBytes);
        QuotaBytes = quotaBytes;
    }

    /// <inheritdoc/>
    public long QuotaBytes { get; }

    /// <summary>Gets the number of bytes currently counted against the quota.</summary>
    public long UsedBytes
    {
        get
        {
            lock (_items)
            {
                return _usedBytes;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PluginStorageKey.ThrowIfInvalid(key);
        lock (_items)
        {
            return ValueTask.FromResult(_items.GetValueOrDefault(key));
        }
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PluginStorageKey.ThrowIfInvalid(key);
        ArgumentNullException.ThrowIfNull(value);
        PluginStorageSize.ThrowIfMalformed(value, nameof(value));
        lock (_items)
        {
            long previous = _items.TryGetValue(key, out string? old) ? Size(key, old) : 0;
            long updated = _usedBytes - previous + Size(key, value);
            if (updated > QuotaBytes)
            {
                throw new PluginStorageException($"Storage quota of {QuotaBytes} bytes exceeded ({updated} bytes requested).");
            }

            _items[key] = value;
            _usedBytes = updated;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PluginStorageKey.ThrowIfInvalid(key);
        lock (_items)
        {
            if (!_items.Remove(key, out string? old))
            {
                return ValueTask.FromResult(false);
            }

            _usedBytes -= Size(key, old);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_items)
        {
            IReadOnlyList<string> keys = [.. _items.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal))];
            return ValueTask.FromResult(keys);
        }
    }

    private static long Size(string key, string value) => PluginStorageSize.Of(key, value);
}
