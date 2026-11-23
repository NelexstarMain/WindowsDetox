using System.Collections.Concurrent;

namespace Blocker.Platform.Windows.Processes;

/// <summary>
/// Gives ETW-reported processes the same start-time key as <c>GetProcessTimes</c>. The provider's <c>CreateTime</c> reaches us as a local
/// <see cref="DateTime"/>, which is an hour off in the repeated hour at the end of daylight saving time, so the key would never match the
/// process the controller opens. The system's own value is authoritative; the payload is only the fallback.
/// </summary>
/// <param name="querySystemStartTicks">Start time (FILETIME ticks, UTC) of a live process, or <see langword="null"/> when it cannot be opened.</param>
internal sealed class ProcessStartTimeResolver(Func<int, long?> querySystemStartTicks)
{
    /// <summary>Upper bound of remembered starts; exits normally remove them, a lost exit event must not leak forever.</summary>
    internal const int MaxRemembered = 65536;

    // Larger than any daylight-saving shift: only guards against a pid that was reused before the query ran.
    private static readonly long PlausibleSkew = TimeSpan.FromHours(2).Ticks;

    private readonly ConcurrentDictionary<int, long> _resolved = new();

    internal int Remembered => _resolved.Count;

    /// <summary>The start-time key for a process-start event.</summary>
    public long ResolveStart(int processId, long payloadTicks)
    {
        long ticks = querySystemStartTicks(processId) is { } real && Math.Abs(real - payloadTicks) <= PlausibleSkew ? real : payloadTicks;
        if (_resolved.Count >= MaxRemembered)
        {
            _resolved.Clear();
        }

        _resolved[processId] = ticks;
        return ticks;
    }

    /// <summary>The start-time key for a process-exit event; the process is gone, so only what the start resolved is available.</summary>
    public long ResolveExit(int processId, long payloadTicks) => _resolved.TryRemove(processId, out long ticks) ? ticks : payloadTicks;
}
