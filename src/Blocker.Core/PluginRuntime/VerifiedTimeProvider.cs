namespace Blocker.Core.PluginRuntime;

/// <summary>
/// The clock plugins see: <see cref="GetUtcNow"/> is the latest verified anchor plus the monotonic time elapsed since it was set,
/// so plugins get network-verified time inside the sandbox without any network access. Before the first anchor it falls back to
/// the underlying clock. Timestamps and timers are the underlying clock's.
/// </summary>
/// <param name="clock">Underlying clock (the system clock in production).</param>
public sealed class VerifiedTimeProvider(TimeProvider clock) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _anchorUtc;
    private long _anchorTimestamp;
    private bool _anchored;

    /// <inheritdoc/>
    public override long TimestampFrequency => clock.TimestampFrequency;

    /// <summary>Sets the anchor: <paramref name="verifiedUtcNow"/> is the verified time right now.</summary>
    /// <param name="verifiedUtcNow">Network-verified UTC time at the moment of the call.</param>
    public void Anchor(DateTimeOffset verifiedUtcNow)
    {
        lock (_gate)
        {
            _anchorUtc = verifiedUtcNow;
            _anchorTimestamp = clock.GetTimestamp();
            _anchored = true;
        }
    }

    /// <inheritdoc/>
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _anchored ? _anchorUtc + clock.GetElapsedTime(_anchorTimestamp, clock.GetTimestamp()) : clock.GetUtcNow();
        }
    }

    /// <inheritdoc/>
    public override long GetTimestamp() => clock.GetTimestamp();

    /// <inheritdoc/>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        clock.CreateTimer(callback, state, dueTime, period);
}
