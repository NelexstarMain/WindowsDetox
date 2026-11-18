namespace Blocker.Core.Ipc;

/// <summary>Thread-safe token bucket: <c>burst</c> tokens, refilled at <c>ratePerSecond</c>.</summary>
internal sealed class TokenBucket
{
    private readonly Lock _gate = new();
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly TimeProvider _time;
    private double _tokens;
    private long _lastTimestamp;

    public TokenBucket(double ratePerSecond, int burst, TimeProvider time)
    {
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _time = time;
        _tokens = burst;
        _lastTimestamp = time.GetTimestamp();
    }

    /// <summary>Takes one token; returns false when the bucket is empty.</summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var elapsed = _time.GetElapsedTime(_lastTimestamp, now);
            _lastTimestamp = now;
            _tokens = Math.Min(_burst, _tokens + (elapsed.TotalSeconds * _ratePerSecond));
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
