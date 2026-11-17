namespace Blocker.Core.Session;

/// <summary>Persisted elapsed-time bookkeeping of one session. No wall-clock value is ever stored except network-verified UTC.</summary>
/// <param name="SessionId">Session id.</param>
/// <param name="PlannedDuration">Planned length.</param>
/// <param name="ElapsedMs">Milliseconds of session time consumed; never decreases.</param>
/// <param name="LastCheckpointTick">Monotonic tick of the last checkpoint.</param>
/// <param name="BootMarker">Caller-provided boot identity (e.g. approximate boot time) of the last checkpoint.</param>
/// <param name="StartUtcVerified">Verified UTC of the session start (backfilled once verified time is first known).</param>
public sealed record SessionClockState(
    Guid SessionId,
    TimeSpan PlannedDuration,
    long ElapsedMs,
    long LastCheckpointTick,
    long BootMarker,
    DateTimeOffset? StartUtcVerified);

/// <summary>
/// Pure session-time logic. Within one boot, elapsed grows by the monotonic tick delta. After a reboot (changed boot marker or
/// a tick that went backwards) powered-off time is not counted, unless verified UTC and the verified start are both known, in which
/// case elapsed is raised to the verified distance. Windows wall-clock changes are never consulted.
/// </summary>
/// <param name="initial">Current state.</param>
public sealed class SessionClock(SessionClockState initial)
{
    /// <summary>Gets the state this clock was built from.</summary>
    public SessionClockState State { get; } = initial;

    /// <summary>Gets the session time left (never negative).</summary>
    public TimeSpan Remaining => TimeSpan.FromMilliseconds(Math.Max(0, State.PlannedDuration.TotalMilliseconds - State.ElapsedMs));

    /// <summary>Gets whether the planned duration has fully elapsed.</summary>
    public bool IsExpired => State.ElapsedMs >= State.PlannedDuration.TotalMilliseconds;

    /// <summary>Creates the initial state of a new session.</summary>
    public static SessionClockState Start(Guid sessionId, TimeSpan plannedDuration, long tick, long bootMarker, DateTimeOffset? verifiedUtc) =>
        new(sessionId, plannedDuration, 0, tick, bootMarker, verifiedUtc);

    /// <summary>Returns the state after accounting for time up to <paramref name="tick"/>.</summary>
    public SessionClockState Checkpoint(long tick, long bootMarker, DateTimeOffset? verifiedUtc)
    {
        var s = State;
        var elapsed = s.ElapsedMs;
        if (bootMarker == s.BootMarker && tick >= s.LastCheckpointTick)
        {
            elapsed += tick - s.LastCheckpointTick;
        }
        else if (verifiedUtc is { } now && s.StartUtcVerified is { } start)
        {
            elapsed = Math.Max(elapsed, (long)(now - start).TotalMilliseconds);
        }

        var startUtc = s.StartUtcVerified ?? verifiedUtc?.AddMilliseconds(-elapsed);
        return s with { ElapsedMs = elapsed, LastCheckpointTick = tick, BootMarker = bootMarker, StartUtcVerified = startUtc };
    }
}
