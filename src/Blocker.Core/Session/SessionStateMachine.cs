using Blocker.Core.Abstractions;
using Blocker.Core.Clock;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;

namespace Blocker.Core.Session;

/// <summary>Result of <see cref="SessionStateMachine.StartAsync"/>.</summary>
/// <param name="Started">Whether a session was started.</param>
/// <param name="SessionId">Id of the new session.</param>
/// <param name="BreakGlassCode">Recovery code; returned exactly once and never stored in plain text.</param>
/// <param name="Errors">Why the start was refused.</param>
public sealed record StartSessionResult(bool Started, Guid? SessionId, string? BreakGlassCode, IReadOnlyList<string> Errors)
{
    internal static StartSessionResult Failed(params string[] errors) => new(false, null, null, errors);
}

/// <summary>Result of <see cref="SessionStateMachine.RequestEmergencyPassAsync"/>.</summary>
public enum EmergencyPassResult
{
    /// <summary>The pass started; enforcement is suspended.</summary>
    Started,

    /// <summary>No passes are left today.</summary>
    NoPassesLeft,

    /// <summary>A pass is already running.</summary>
    AlreadyActive,

    /// <summary>No session is running.</summary>
    NotInSession,
}

/// <summary>What happened during one tick, so the service can toast, close apps or roll back.</summary>
/// <param name="PassWarningDue">The running pass has at most 60 s left (raised once per pass).</param>
/// <param name="PassEnded">A pass ended in this tick (also when the session ended during a pass); enforcement resumes.</param>
/// <param name="SessionExpired">The session expired and was ended; roll back.</param>
public readonly record struct TickOutcome(bool PassWarningDue, bool PassEnded, bool SessionExpired)
{
    /// <summary>Gets the outcome of a tick where nothing noteworthy happened.</summary>
    public static TickOutcome None => default;
}

/// <summary>
/// The only authority over a session's lifetime: start, emergency passes, expiry and break-glass. A session cannot be shortened or
/// cancelled any other way (developer abort exists only when enabled by a DEVMODE build). State is persisted on every tick. Thread-safe.
/// Event timestamps use verified UTC, or <see cref="DateTimeOffset.UnixEpoch"/> while none is available; the wall clock is never read.
/// </summary>
/// <param name="store">Durable session storage.</param>
/// <param name="events">Event publisher.</param>
/// <param name="clock">Monotonic clock.</param>
/// <param name="verified">Verified UTC source.</param>
/// <param name="bootMarker">Returns an identifier of the current boot (e.g. approximate boot time).</param>
/// <param name="allowDeveloperAbort">Enables <see cref="TryDeveloperAbortAsync"/> (DEVMODE builds only).</param>
/// <param name="breakGlassIterations">PBKDF2 iterations for the break-glass hash.</param>
public sealed class SessionStateMachine(
    ISessionStore store,
    IEventPublisher events,
    IMonotonicClock clock,
    IVerifiedClock verified,
    Func<long> bootMarker,
    bool allowDeveloperAbort,
    int breakGlassIterations = BreakGlassCode.DefaultIterations) : ISessionInfo
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PersistedSession? _session;
    private bool _warningRaised;
    private volatile SessionSnapshot _snapshot = SessionSnapshot.Idle;

    /// <summary>Gets the latest snapshot (refreshed by every operation; never blocks).</summary>
    public SessionSnapshot Snapshot => _snapshot;

    /// <inheritdoc/>
    SessionSnapshot ISessionInfo.Current => _snapshot;

    /// <summary>Starts a session; only possible from Idle and only when no persisted session exists.</summary>
    public async ValueTask<StartSessionResult> StartAsync(SessionConfig config, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null || await store.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
            {
                return StartSessionResult.Failed("A session is already running.");
            }

            if (SessionConfigValidator.Validate(config) is { Count: > 0 } errors)
            {
                return new(false, null, null, errors);
            }

            var tick = clock.TickCount64;
            var utc = Verified();
            var id = Guid.NewGuid();
            var (code, hash) = BreakGlassCode.Generate(breakGlassIterations);
            var session = new PersistedSession(
                config,
                SessionClock.Start(id, config.Duration, tick, bootMarker(), utc),
                EmergencyPassLedger.Create(config, tick, utc).State,
                hash,
                SessionPhase.Active);
            await store.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            _session = session;
            _warningRaised = false;
            Publish(session);
            await events.PublishAsync(new SessionStarted(id, config.Duration, EventTime()), cancellationToken).ConfigureAwait(false);
            return new(true, id, code, []);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts an emergency pass if allowed.</summary>
    public async ValueTask<EmergencyPassResult> RequestEmergencyPassAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                return EmergencyPassResult.NotInSession;
            }

            await TickCoreAsync(cancellationToken).ConfigureAwait(false);
            if (_session is not { } current)
            {
                return EmergencyPassResult.NotInSession; // expired just now
            }

            var ledger = new EmergencyPassLedger(current.Ledger);
            if (ledger.IsPassActive)
            {
                return EmergencyPassResult.AlreadyActive;
            }

            if (!ledger.TryStartPass(current.Ledger.LastTick))
            {
                return EmergencyPassResult.NoPassesLeft;
            }

            _session = current with { Ledger = ledger.State, Phase = SessionPhase.EmergencyPass };
            _warningRaised = false;
            await store.SaveAsync(_session, cancellationToken).ConfigureAwait(false);
            Publish(_session);
            await events.PublishAsync(
                new EmergencyPassStarted(current.Clock.SessionId, current.Config.EmergencyPassDuration, ledger.PassesLeft, EventTime()),
                cancellationToken).ConfigureAwait(false);
            return EmergencyPassResult.Started;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Checkpoints the clock, updates the pass ledger, persists, and reports what changed. Call about once a second.</summary>
    public async ValueTask<TickOutcome> TickAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _session is null ? TickOutcome.None : await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session with the one-time recovery code; returns <see langword="false"/> for a wrong code or no session.</summary>
    public async ValueTask<bool> TryBreakGlassAsync(string code, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not { } current || !BreakGlassCode.Verify(code, current.BreakGlass))
            {
                return false;
            }

            await EndAsync(current, SessionEndReason.BreakGlass, current.Ledger.PassActive, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the session without a code; always <see langword="false"/> unless developer abort was enabled.</summary>
    public async ValueTask<bool> TryDeveloperAbortAsync(CancellationToken cancellationToken = default)
    {
        if (!allowDeveloperAbort)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not { } current)
            {
                return false;
            }

            await EndAsync(current, SessionEndReason.DeveloperAbort, current.Ledger.PassActive, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads a persisted session after a service restart or reboot and immediately checkpoints it (reboot rules apply; an expired
    /// session is ended). Check <see cref="Snapshot"/> afterwards to see whether a session is running.
    /// </summary>
    public async ValueTask<TickOutcome> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                _session = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
                _warningRaised = false;
            }

            if (_session is null)
            {
                _snapshot = SessionSnapshot.Idle;
                return TickOutcome.None;
            }

            return await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate and _session is not null.
    private async ValueTask<TickOutcome> TickCoreAsync(CancellationToken cancellationToken)
    {
        var current = _session!;
        var tick = clock.TickCount64;
        var boot = bootMarker();
        var utc = Verified();
        var rebooted = boot != current.Clock.BootMarker || tick < current.Clock.LastCheckpointTick;
        var sessionClock = new SessionClock(new SessionClock(current.Clock).Checkpoint(tick, boot, utc));
        var ledger = new EmergencyPassLedger(current.Ledger);
        var passEnded = ledger.Update(tick, utc, rebooted);
        current = current with { Clock = sessionClock.State, Ledger = ledger.State };

        if (sessionClock.IsExpired)
        {
            var passOver = passEnded || ledger.IsPassActive;
            await EndAsync(current, SessionEndReason.Expired, passOver, cancellationToken).ConfigureAwait(false);
            return new(false, passOver, true);
        }

        current = current with { Phase = ledger.IsPassActive ? SessionPhase.EmergencyPass : SessionPhase.Active };
        var warning = ledger.WarningDue && !_warningRaised;
        _warningRaised = ledger.WarningDue;
        _session = current;
        await store.SaveAsync(current, cancellationToken).ConfigureAwait(false);
        Publish(current);
        if (passEnded)
        {
            await events.PublishAsync(new EmergencyPassEnded(current.Clock.SessionId, EventTime()), cancellationToken).ConfigureAwait(false);
        }

        return new(warning, passEnded, false);
    }

    private async ValueTask EndAsync(PersistedSession session, SessionEndReason reason, bool passOver, CancellationToken cancellationToken)
    {
        await store.ClearAsync(cancellationToken).ConfigureAwait(false);
        _session = null;
        _warningRaised = false;
        _snapshot = SessionSnapshot.Idle;
        var id = session.Clock.SessionId;
        if (passOver)
        {
            await events.PublishAsync(new EmergencyPassEnded(id, EventTime()), cancellationToken).ConfigureAwait(false);
        }

        await events.PublishAsync(new SessionEnded(id, reason, EventTime()), cancellationToken).ConfigureAwait(false);
    }

    private void Publish(PersistedSession s)
    {
        var ledger = new EmergencyPassLedger(s.Ledger);
        _snapshot = new SessionSnapshot(
            s.Phase,
            s.Clock.SessionId,
            new SessionClock(s.Clock).Remaining,
            ledger.IsPassActive ? ledger.PassRemaining : null,
            ledger.PassesLeft,
            s.Config.EmergencyPassesPerDay,
            s.Config.EmergencyPassDuration);
    }

    private DateTimeOffset? Verified() => verified.TryGetUtcNow(out var utc) ? utc : null;

    private DateTimeOffset EventTime() => Verified() ?? DateTimeOffset.UnixEpoch;
}
