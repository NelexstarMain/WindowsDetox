namespace Blocker.Platform.Windows.Processes;

/// <summary>Lets one report through per interval and counts what it held back, so a flood of identical events yields one log line with a total.</summary>
internal sealed class ReportThrottle(TimeSpan interval, Func<long>? tickCount = null)
{
    private readonly Func<long> _tickCount = tickCount ?? (() => Environment.TickCount64);
    private readonly Lock _gate = new();
    private long _lastReport;
    private bool _reportedBefore;
    private int _suppressed;

    /// <summary>True when the caller should report now; <paramref name="suppressed"/> is how many calls were held back since the last report.</summary>
    public bool TryAcquire(out int suppressed)
    {
        lock (_gate)
        {
            long now = _tickCount();
            if (_reportedBefore && now - _lastReport < interval.TotalMilliseconds)
            {
                _suppressed++;
                suppressed = 0;
                return false;
            }

            _reportedBefore = true;
            _lastReport = now;
            suppressed = _suppressed;
            _suppressed = 0;
            return true;
        }
    }
}
