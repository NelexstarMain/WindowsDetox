using Blocker.Sdk.Session;

namespace Blocker.UI.ViewModels;

/// <summary>
/// Keeps the latest session snapshot together with the moment it was received so the UI can count down smoothly between
/// service pushes. The service stays authoritative: every new snapshot replaces the local estimate.
/// </summary>
internal sealed class SessionTimeline(TimeProvider time)
{
    private long _receivedAt = time.GetTimestamp();

    /// <summary>Gets the latest snapshot.</summary>
    public SessionSnapshot Snapshot { get; private set; } = SessionSnapshot.Idle;

    /// <summary>Gets the estimated time until the session ends, or <see langword="null"/> when idle.</summary>
    public TimeSpan? Remaining => Decay(Snapshot.Remaining);

    /// <summary>Gets the estimated time left on the emergency pass, or <see langword="null"/> when none runs.</summary>
    public TimeSpan? PassRemaining => Decay(Snapshot.EmergencyPassRemaining);

    /// <summary>Replaces the snapshot and restarts local counting.</summary>
    /// <param name="snapshot">Snapshot received from the service.</param>
    public void Update(SessionSnapshot snapshot)
    {
        Snapshot = snapshot;
        _receivedAt = time.GetTimestamp();
    }

    private TimeSpan? Decay(TimeSpan? value) =>
        value is { } span ? TimeSpan.FromTicks(Math.Max(0, (span - time.GetElapsedTime(_receivedAt)).Ticks)) : null;
}
