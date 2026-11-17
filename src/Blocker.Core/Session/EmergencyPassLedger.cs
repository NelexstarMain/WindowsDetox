namespace Blocker.Core.Session;

/// <summary>Persisted emergency-pass bookkeeping.</summary>
/// <param name="Config">Session configuration (passes per day, pass length, carry-over).</param>
/// <param name="FrozenTimeZoneId">Zone captured at session start; the only zone used to derive the day key.</param>
/// <param name="DayKey">Day (in the frozen zone) the pass count was last refilled for; <see langword="null"/> until verified time is first seen.</param>
/// <param name="PassesLeft">Passes available now.</param>
/// <param name="PassActive">Whether a pass is currently running.</param>
/// <param name="PassRemainingMs">Milliseconds left on the running pass.</param>
/// <param name="LastTick">Monotonic tick of the last update.</param>
public sealed record EmergencyPassLedgerState(
    SessionConfig Config,
    string FrozenTimeZoneId,
    DateOnly? DayKey,
    int PassesLeft,
    bool PassActive,
    long PassRemainingMs,
    long LastTick);

/// <summary>
/// Pure emergency-pass logic. The day key comes only from network-verified UTC converted by the frozen zone, so changing the Windows
/// clock or zone cannot grant passes; without verified time nothing is refilled. A running pass is timed on monotonic ticks and ends
/// conservatively if the tick counter went backwards or a reboot is reported. Mutated only by its owner (not thread-safe).
/// </summary>
/// <param name="initial">Initial state.</param>
public sealed class EmergencyPassLedger(EmergencyPassLedgerState initial)
{
    /// <summary>Remaining time at or below which <see cref="WarningDue"/> becomes true.</summary>
    public static readonly TimeSpan WarningThreshold = TimeSpan.FromSeconds(60);

    private readonly TimeZoneInfo _zone = SessionConfigValidator.TryResolveZone(initial.FrozenTimeZoneId) ?? TimeZoneInfo.Utc;

    /// <summary>Gets the current persistable state.</summary>
    public EmergencyPassLedgerState State { get; private set; } = initial;

    /// <summary>Gets the passes available now.</summary>
    public int PassesLeft => State.PassesLeft;

    /// <summary>Gets whether a pass is running.</summary>
    public bool IsPassActive => State.PassActive;

    /// <summary>Gets the time left on the running pass; <see cref="TimeSpan.Zero"/> when none.</summary>
    public TimeSpan PassRemaining => State.PassActive ? TimeSpan.FromMilliseconds(Math.Max(0, State.PassRemainingMs)) : TimeSpan.Zero;

    /// <summary>Gets whether a running pass has at most <see cref="WarningThreshold"/> left.</summary>
    public bool WarningDue => State.PassActive && PassRemaining <= WarningThreshold;

    /// <summary>Creates a ledger for a new session; the initial day's passes are granted even before time is verified.</summary>
    public static EmergencyPassLedger Create(SessionConfig config, long tick, DateTimeOffset? verifiedUtc)
    {
        var ledger = new EmergencyPassLedger(new(config, config.TimeZoneId, null, config.EmergencyPassesPerDay, false, 0, tick));
        ledger.RollDay(verifiedUtc);
        return ledger;
    }

    /// <summary>Starts a pass if none is running and one is left.</summary>
    public bool TryStartPass(long tick)
    {
        if (State.PassActive || State.PassesLeft <= 0)
        {
            return false;
        }

        State = State with
        {
            PassActive = true,
            PassesLeft = State.PassesLeft - 1,
            PassRemainingMs = (long)State.Config.EmergencyPassDuration.TotalMilliseconds,
            LastTick = tick,
        };
        return true;
    }

    /// <summary>Advances the running pass and applies a day rollover; returns <see langword="true"/> when a pass ended in this call.</summary>
    /// <param name="tick">Current monotonic tick.</param>
    /// <param name="verifiedUtc">Verified UTC, if known.</param>
    /// <param name="rebooted">The machine rebooted since the last update; a running pass ends.</param>
    public bool Update(long tick, DateTimeOffset? verifiedUtc, bool rebooted = false)
    {
        RollDay(verifiedUtc);
        var passEnded = false;
        if (State.PassActive)
        {
            var remaining = State.PassRemainingMs - (tick - State.LastTick);
            passEnded = rebooted || tick < State.LastTick || remaining <= 0;
            State = State with { PassActive = !passEnded, PassRemainingMs = passEnded ? 0 : remaining };
        }

        State = State with { LastTick = tick };
        return passEnded;
    }

    private void RollDay(DateTimeOffset? verifiedUtc)
    {
        if (verifiedUtc is not { } utc)
        {
            return;
        }

        var key = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _zone).DateTime);
        if (State.DayKey is not { } current)
        {
            State = State with { DayKey = key };
            return;
        }

        var days = key.DayNumber - current.DayNumber;
        if (days <= 0)
        {
            return;
        }

        var perDay = State.Config.EmergencyPassesPerDay;
        var passes = State.Config.CarryOverUnusedPasses ? (int)Math.Min(3L * perDay, State.PassesLeft + (long)perDay * days) : perDay;
        State = State with { DayKey = key, PassesLeft = passes };
    }
}
