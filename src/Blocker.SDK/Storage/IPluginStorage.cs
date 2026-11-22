using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Blocker.Sdk.Storage;

/// <summary>
/// Private key/value storage of a plugin, isolated from other plugins and limited by <see cref="QuotaBytes"/>.
/// Values are UTF-8 strings; use <see cref="PluginStorageExtensions"/> for JSON objects.
/// </summary>
public interface IPluginStorage
{
    /// <summary>Gets the maximum total size of all keys and values, in bytes.</summary>
    long QuotaBytes { get; }

    /// <summary>Reads a value.</summary>
    /// <param name="key">Key; see <see cref="PluginStorageKey.IsValid"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="null"/> when the key does not exist.</returns>
    /// <exception cref="ArgumentException">The key is invalid.</exception>
    /// <exception cref="PluginStorageException">The storage could not be read.</exception>
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a value.</summary>
    /// <param name="key">Key; see <see cref="PluginStorageKey.IsValid"/>.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the value is durable.</returns>
    /// <exception cref="ArgumentException">The key is invalid, or the value contains an unpaired UTF-16 surrogate.</exception>
    /// <exception cref="PluginStorageException">The quota would be exceeded or the storage could not be written.</exception>
    ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a value.</summary>
    /// <param name="key">Key to delete.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns><see langword="true"/> when the key existed.</returns>
    /// <exception cref="ArgumentException">The key is invalid.</exception>
    /// <exception cref="PluginStorageException">The storage could not be read or written.</exception>
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Lists keys, optionally filtered by an ordinal prefix, in ordinal order.</summary>
    /// <param name="prefix">Prefix filter, or <see langword="null"/> for all keys.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>Matching keys.</returns>
    /// <exception cref="PluginStorageException">The storage could not be read.</exception>
    ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default);
}

/// <summary>Validation rules for storage keys.</summary>
public static partial class PluginStorageKey
{
    /// <summary>Maximum key length.</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// Returns whether <paramref name="key"/> is valid: 1-128 characters from <c>a-z</c>, <c>0-9</c>,
    /// <c>.</c>, <c>_</c> and <c>-</c>, not starting with a dot.
    /// </summary>
    /// <param name="key">Candidate key.</param>
    /// <returns><see langword="true"/> when valid.</returns>
    public static bool IsValid([NotNullWhen(true)] string? key) =>
        key is { Length: > 0 and <= MaxLength } && KeyPattern().IsMatch(key);

    /// <summary>Throws when <paramref name="key"/> is invalid.</summary>
    /// <param name="key">Key to validate.</param>
    /// <exception cref="ArgumentException">The key is invalid.</exception>
    public static void ThrowIfInvalid([NotNull] string? key)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException($"'{key}' is not a valid storage key (allowed: a-z 0-9 . _ -, max {MaxLength}).", nameof(key));
        }
    }

    [GeneratedRegex("^[a-z0-9_-][a-z0-9._-]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Thrown when a storage operation fails, for example because the quota is exhausted or the file is unreadable.</summary>
public sealed class PluginStorageException : IOException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error description.</param>
    public PluginStorageException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the failure that caused it.</summary>
    /// <param name="message">Error description; must not contain host paths.</param>
    /// <param name="innerException">The underlying failure.</param>
    public PluginStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>JSON helpers for <see cref="IPluginStorage"/>.</summary>
public static class PluginStorageExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Reads and deserializes a JSON value.</summary>
    /// <typeparam name="T">Target type.</typeparam>
    /// <param name="storage">Storage instance.</param>
    /// <param name="key">Key to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The value, or <see langword="default"/> when the key does not exist.</returns>
    /// <exception cref="JsonException">The stored value is not valid JSON for <typeparamref name="T"/>.</exception>
    public static async ValueTask<T?> ReadJsonAsync<T>(this IPluginStorage storage, string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        string? json = await storage.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, Options);
    }

    /// <summary>Serializes and stores a value as JSON.</summary>
    /// <typeparam name="T">Value type.</typeparam>
    /// <param name="storage">Storage instance.</param>
    /// <param name="key">Key to write.</param>
    /// <param name="value">Value to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the value is durable.</returns>
    public static ValueTask WriteJsonAsync<T>(this IPluginStorage storage, string key, T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return storage.WriteAsync(key, JsonSerializer.Serialize(value, Options), cancellationToken);
    }
}
