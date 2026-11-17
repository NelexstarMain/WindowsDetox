using Blocker.Sdk.Session;

namespace Blocker.Core.Session;

/// <summary>Everything needed to resume a session after a service restart or reboot.</summary>
/// <param name="Config">Session configuration.</param>
/// <param name="Clock">Elapsed-time state.</param>
/// <param name="Ledger">Emergency-pass state.</param>
/// <param name="BreakGlass">Verifier of the break-glass code.</param>
/// <param name="Phase">Phase (<see cref="SessionPhase.Active"/> or <see cref="SessionPhase.EmergencyPass"/>).</param>
public sealed record PersistedSession(
    SessionConfig Config,
    SessionClockState Clock,
    EmergencyPassLedgerState Ledger,
    BreakGlassHash BreakGlass,
    SessionPhase Phase);

/// <summary>Durable storage of the running session (an integrity-protected file in production).</summary>
public interface ISessionStore
{
    /// <summary>Loads the persisted session, or <see langword="null"/> when none exists.</summary>
    ValueTask<PersistedSession?> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically replaces the persisted session.</summary>
    ValueTask SaveAsync(PersistedSession session, CancellationToken cancellationToken = default);

    /// <summary>Removes the persisted session.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Non-durable <see cref="ISessionStore"/> for tests and development.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private volatile PersistedSession? _session;

    /// <inheritdoc/>
    public ValueTask<PersistedSession?> LoadAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_session);

    /// <inheritdoc/>
    public ValueTask SaveAsync(PersistedSession session, CancellationToken cancellationToken = default)
    {
        _session = session;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        _session = null;
        return ValueTask.CompletedTask;
    }
}
