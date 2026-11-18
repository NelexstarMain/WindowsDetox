using System.Text.Json.Serialization;
using Blocker.Core.Policy;
using Blocker.Core.Security;

namespace Blocker.Core.Orchestration;

/// <summary>The user's choices for a session that must survive a service restart: allowed applications and web domains.</summary>
/// <param name="Entries">Allowlisted applications.</param>
/// <param name="Domains">Allowed domain patterns as typed (<c>example.com</c>, <c>*.example.com</c>).</param>
public sealed record SessionPolicy(IReadOnlyList<AllowlistEntry> Entries, IReadOnlyList<string> Domains);

/// <summary>Durable storage of the running session's <see cref="SessionPolicy"/> (the session store itself does not hold it).</summary>
public interface ISessionPolicyStore
{
    /// <summary>Loads the policy, or <see langword="null"/> when none is stored.</summary>
    /// <exception cref="TamperDetectedException">The stored policy failed verification.</exception>
    ValueTask<SessionPolicy?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically replaces the stored policy.</summary>
    ValueTask SaveAsync(SessionPolicy policy, CancellationToken cancellationToken);

    /// <summary>Removes the stored policy.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken);
}

/// <summary>Non-durable <see cref="ISessionPolicyStore"/> for tests and development.</summary>
public sealed class InMemorySessionPolicyStore : ISessionPolicyStore
{
    private volatile SessionPolicy? _policy;

    /// <inheritdoc/>
    public ValueTask<SessionPolicy?> LoadAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_policy);

    /// <inheritdoc/>
    public ValueTask SaveAsync(SessionPolicy policy, CancellationToken cancellationToken)
    {
        _policy = policy;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        _policy = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Stores the policy in an integrity-protected file (<see cref="SecureFileStore"/>).</summary>
/// <param name="store">Secure store.</param>
/// <param name="path">File path.</param>
public sealed class SecureSessionPolicyStore(SecureFileStore store, string path) : ISessionPolicyStore
{
    /// <inheritdoc/>
    public ValueTask<SessionPolicy?> LoadAsync(CancellationToken cancellationToken) =>
        store.ReadJsonAsync(path, SessionPolicyJsonContext.Default.SessionPolicy, cancellationToken);

    /// <inheritdoc/>
    public ValueTask SaveAsync(SessionPolicy policy, CancellationToken cancellationToken) =>
        store.WriteJsonAsync(path, policy, SessionPolicyJsonContext.Default.SessionPolicy, cancellationToken);

    /// <inheritdoc/>
    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        store.Delete(path);
        return ValueTask.CompletedTask;
    }
}

[JsonSerializable(typeof(SessionPolicy))]
internal sealed partial class SessionPolicyJsonContext : JsonSerializerContext;
