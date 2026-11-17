using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Blocker.Core.Abstractions;

namespace Blocker.Core.Security;

/// <summary>Atomic, protected and MAC-verified file persistence built on <see cref="SecureEnvelope"/>.</summary>
public sealed class SecureFileStore(IFileSystem fileSystem, ISecretProtector protector, HmacKeyStore keys)
{
    /// <summary>Seals <paramref name="payload"/> and atomically replaces <paramref name="path"/> (temp file, flush, move).</summary>
    public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] blob = SecureEnvelope.Seal(payload.Span, protector, keys.GetKey());
        return AtomicFile.WriteAsync(fileSystem, path, blob, cancellationToken);
    }

    /// <summary>Reads and verifies <paramref name="path"/>; null when the file does not exist.</summary>
    /// <exception cref="TamperDetectedException">The file is not a valid envelope for the current key.</exception>
    public async ValueTask<byte[]?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(path))
        {
            return null;
        }

        byte[] blob = await fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return SecureEnvelope.Open(blob, protector, keys.GetKey());
    }

    /// <summary>Serializes <paramref name="value"/> with source-generated metadata and writes it sealed.</summary>
    public ValueTask WriteJsonAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken) =>
        WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), cancellationToken);

    /// <summary>Reads a sealed JSON document; null when missing.</summary>
    /// <exception cref="TamperDetectedException">Envelope verification failed.</exception>
    /// <exception cref="JsonException">The verified payload is not valid JSON for <typeparamref name="T"/>.</exception>
    public async ValueTask<T?> ReadJsonAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        byte[]? payload = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return payload is null ? default : JsonSerializer.Deserialize(payload, typeInfo);
    }

    /// <summary>Deletes <paramref name="path"/> if present.</summary>
    public void Delete(string path)
    {
        if (fileSystem.File.Exists(path))
        {
            fileSystem.File.Delete(path);
        }
    }
}
