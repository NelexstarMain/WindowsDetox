using System.IO.Abstractions;
using System.Text.Json;
using Blocker.Core.Security;
using Blocker.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>
/// <see cref="IPluginStorage"/> persisted as one JSON file. Same rules as the testing fake: keys are validated with
/// <see cref="PluginStorageKey"/> and the quota counts <c>(key length + value length) * 2</c> bytes per entry. Every write
/// replaces the file atomically (temp file, flush, move) and the in-memory state only changes once the file is durable.
/// </summary>
public sealed partial class FileBackedPluginStorage : IPluginStorage, IDisposable
{
    /// <summary>Default quota: 1 MiB.</summary>
    public const long DefaultQuotaBytes = 1024 * 1024;

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _items;
    private long _usedBytes;

    /// <summary>Creates storage backed by <paramref name="path"/>; the file is read on first use.</summary>
    /// <param name="fileSystem">File system.</param>
    /// <param name="path">Path of <c>storage.json</c>.</param>
    /// <param name="logger">Logger for damaged files.</param>
    /// <param name="quotaBytes">Maximum total size of all keys and values, in bytes.</param>
    public FileBackedPluginStorage(IFileSystem fileSystem, string path, ILogger logger, long quotaBytes = DefaultQuotaBytes)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(quotaBytes);
        _fileSystem = fileSystem;
        _path = path;
        _logger = logger;
        QuotaBytes = quotaBytes;
    }

    /// <inheritdoc/>
    public long QuotaBytes { get; }

    /// <inheritdoc/>
    public async ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginStorageKey.ThrowIfInvalid(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        PluginStorageKey.ThrowIfInvalid(key);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> items = await LoadAsync(cancellationToken).ConfigureAwait(false);
            long previous = items.TryGetValue(key, out string? old) ? Size(key, old) : 0;
            long updated = _usedBytes - previous + Size(key, value);
            if (updated > QuotaBytes)
            {
                throw new PluginStorageException($"Storage quota of {QuotaBytes} bytes exceeded ({updated} bytes requested).");
            }

            await CommitAsync(items, key, value, updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        PluginStorageKey.ThrowIfInvalid(key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> items = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!items.TryGetValue(key, out string? old))
            {
                return false;
            }

            await CommitAsync(items, key, null, _usedBytes - Size(key, old), cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> items = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. items.Keys.Where(k => prefix is null || k.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private static long Size(string key, string value) => ((long)key.Length + value.Length) * 2;

    // Writes the changed map first; only a durable file changes the in-memory state.
    private async ValueTask CommitAsync(Dictionary<string, string> items, string key, string? value, long usedBytes, CancellationToken cancellationToken)
    {
        Dictionary<string, string> next = new(items, StringComparer.Ordinal);
        if (value is null)
        {
            next.Remove(key);
        }
        else
        {
            next[key] = value;
        }

        try
        {
            await AtomicFile.WriteAsync(
                _fileSystem,
                _path,
                JsonSerializer.SerializeToUtf8Bytes(next, PluginRuntimeJsonContext.Default.DictionaryStringString),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginStorageException("The storage could not be written.");
        }

        _items = next;
        _usedBytes = usedBytes;
    }

    private async ValueTask<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_items is { } loaded)
        {
            return loaded;
        }

        Dictionary<string, string> items = new(StringComparer.Ordinal);
        if (_fileSystem.File.Exists(_path))
        {
            try
            {
                byte[] bytes = await _fileSystem.File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
                foreach ((string key, string value) in JsonSerializer.Deserialize(bytes, PluginRuntimeJsonContext.Default.DictionaryStringString) ?? [])
                {
                    if (PluginStorageKey.IsValid(key) && value is not null)
                    {
                        items[key] = value;
                    }
                }
            }
            catch (JsonException ex)
            {
                LogDamaged(_logger, ex);
                items.Clear();
            }
        }

        _usedBytes = items.Sum(pair => Size(pair.Key, pair.Value));
        _items = items;
        return items;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin storage file is damaged; starting empty.")]
    private static partial void LogDamaged(ILogger logger, Exception exception);
}
