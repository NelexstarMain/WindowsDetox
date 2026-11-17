namespace Blocker.Sdk.Session;

/// <summary>Phase of the detox session state machine.</summary>
public enum SessionPhase
{
    /// <summary>No session is running; nothing is enforced.</summary>
    Idle,

    /// <summary>A session is running and the allowlist is enforced.</summary>
    Active,

    /// <summary>A session is running but enforcement is suspended by an emergency pass.</summary>
    EmergencyPass,
}

/// <summary>
/// Immutable view of the session state at one point in time. All durations are measured on the monotonic
/// clock and are immune to changes of the Windows date, time or time zone.
/// </summary>
/// <param name="Phase">Current phase.</param>
/// <param name="SessionId">Id of the running session; <see langword="null"/> when <see cref="SessionPhase.Idle"/>.</param>
/// <param name="Remaining">Time until the session ends; <see langword="null"/> when idle.</param>
/// <param name="EmergencyPassRemaining">Time left on the active emergency pass; <see langword="null"/> otherwise.</param>
/// <param name="EmergencyPassesLeftToday">Emergency passes still available today.</param>
/// <param name="EmergencyPassesPerDay">Passes granted per day by the session configuration.</param>
/// <param name="EmergencyPassDuration">Length of a single emergency pass.</param>
public sealed record SessionSnapshot(
    SessionPhase Phase,
    Guid? SessionId,
    TimeSpan? Remaining,
    TimeSpan? EmergencyPassRemaining,
    int EmergencyPassesLeftToday,
    int EmergencyPassesPerDay,
    TimeSpan EmergencyPassDuration)
{
    /// <summary>Gets the snapshot that represents "no session running".</summary>
    public static SessionSnapshot Idle { get; } = new(SessionPhase.Idle, null, null, null, 0, 0, TimeSpan.Zero);

    /// <summary>Gets whether a session is running (including during an emergency pass).</summary>
    public bool IsSessionRunning => Phase is SessionPhase.Active or SessionPhase.EmergencyPass;
}

/// <summary>Read-only access to the session state.</summary>
public interface ISessionInfo
{
    /// <summary>Gets the latest session snapshot. Cheap to call; never blocks.</summary>
    SessionSnapshot Current { get; }
}
