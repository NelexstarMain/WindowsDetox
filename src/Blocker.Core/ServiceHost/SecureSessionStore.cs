using System.Text.Json.Serialization;
using Blocker.Core.Security;
using Blocker.Core.Session;

namespace Blocker.Core.ServiceHost;

/// <summary>Stores the running session in an integrity-protected file (<see cref="SecureFileStore"/>: machine-bound protection plus HMAC).</summary>
/// <param name="store">Secure store.</param>
/// <param name="path">File path.</param>
public sealed class SecureSessionStore(SecureFileStore store, string path) : ISessionStore
{
    /// <inheritdoc/>
    /// <exception cref="TamperDetectedException">The file failed verification.</exception>
    public ValueTask<PersistedSession?> LoadAsync(CancellationToken cancellationToken = default) =>
        store.ReadJsonAsync(path, SessionStoreJsonContext.Default.PersistedSession, cancellationToken);

    /// <inheritdoc/>
    public ValueTask SaveAsync(PersistedSession session, CancellationToken cancellationToken = default) =>
        store.WriteJsonAsync(path, session, SessionStoreJsonContext.Default.PersistedSession, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        store.Delete(path);
        return ValueTask.CompletedTask;
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(PersistedSession))]
internal sealed partial class SessionStoreJsonContext : JsonSerializerContext;
