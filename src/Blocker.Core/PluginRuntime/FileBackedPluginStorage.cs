using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using Blocker.Core.Security;
using Blocker.Sdk.Storage;
using Microsoft.Extensions.Logging;

namespace Blocker.Core.PluginRuntime;

/// <summary>
/// <see cref="IPluginStorage"/> persisted as one JSON file. Same rules as the testing fake: keys are validated with
/// <see cref="PluginStorageKey"/>, values must be well-formed UTF-16 and the quota counts the UTF-8 size of the
/// JSON-escaped key and value per entry. Every write replaces the file atomically (temp file, flush, move) and the
/// in-memory state only changes once the file is durable. A file that does not parse is never overwritten: it is renamed
/// to <c>storage.json.{UTC yyyyMMddHHmmss}.bad</c> (the three newest copies are kept) and the storage starts empty.
/// </summary>
public sealed partial class FileBackedPluginStorage : IPluginStorage, IDisposable
{
    /// <summary>Default quota: 1 MiB.</summary>
    public const long DefaultQuotaBytes = 1024 * 1024;

    private const int KeptDamagedCopies = 3;

    private readonly IFileSystem _fileSystem;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _items;
    private long _usedBytes;

    /// <summary>Creates storage backed by <paramref name="path"/>; the file is read on first use.</summary>
    /// <param name="fileSystem">File system.</param>
    /// <param name="path">Path of <c>storage.json</c>.</param>
    /// <param name="logger">Logger for damaged files.</param>
    /// <param name="quotaBytes">Maximum total size of all keys and values, in bytes.</param>
    /// <param name="timeProvider">Clock for the names of set-aside damaged files; defaults to the system clock.</param>
    public FileBackedPluginStorage(IFileSystem fileSystem, string path, ILogger logger, long quotaBytes = DefaultQuotaBytes, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(quotaBytes);
        _fileSystem = fileSystem;
        _path = path;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        PluginStorageSize.ThrowIfMalformed(value, nameof(value));
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

    private static long Size(string key, string value) => PluginStorageSize.Of(key, value);

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

        byte[] json;
        try
        {
            json = JsonSerializer.SerializeToUtf8Bytes(next, PluginRuntimeJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new PluginStorageException("The storage content could not be encoded.", ex);
        }

        try
        {
            await AtomicFile.WriteAsync(_fileSystem, _path, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginStorageException("The storage could not be written.", ex);
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
            byte[] bytes;
            try
            {
                bytes = await _fileSystem.File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PluginStorageException("The storage could not be read.", ex);
            }

            try
            {
                foreach ((string key, string value) in JsonSerializer.Deserialize(bytes, PluginRuntimeJsonContext.Default.DictionaryStringString) ?? [])
                {
                    // A value with an unpaired surrogate (possible via a \ud800 escape) could never be written back.
                    if (PluginStorageKey.IsValid(key) && value is not null && PluginStorageSize.IsWellFormed(value))
                    {
                        items[key] = value;
                    }
                }
            }
            catch (JsonException ex)
            {
                SetDamagedFileAside(ex);
                items.Clear();
            }
        }

        _usedBytes = items.Sum(pair => Size(pair.Key, pair.Value));
        _items = items;
        return items;
    }

    // The next write replaces storage.json, so the damaged content has to be moved out of the way first.
    private void SetDamagedFileAside(JsonException cause)
    {
        string copy = string.Create(CultureInfo.InvariantCulture, $"{_path}.{_timeProvider.GetUtcNow():yyyyMMddHHmmss}.bad");
        try
        {
            _fileSystem.File.Move(_path, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PluginStorageException("The damaged storage file could not be set aside.", ex);
        }

        LogDamaged(_logger, copy, cause);
        PruneDamagedCopies();
    }

    private void PruneDamagedCopies()
    {
        string directory = _fileSystem.Path.GetDirectoryName(_path) ?? string.Empty;
        string pattern = $"{_fileSystem.Path.GetFileName(_path)}.*.bad";
        try
        {
            // The UTC timestamp in the name sorts chronologically.
            foreach (string old in _fileSystem.Directory.GetFiles(directory, pattern).OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeptDamagedCopies))
            {
                _fileSystem.File.Delete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPruneFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin storage file is damaged; moved it to {CopyPath} and starting empty.")]
    private static partial void LogDamaged(ILogger logger, string copyPath, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Old damaged plugin storage copies could not be deleted.")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
