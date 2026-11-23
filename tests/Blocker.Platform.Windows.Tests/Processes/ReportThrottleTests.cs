using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class ReportThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    [Fact]
    public void TheFirstReportPasses_ThenTheFloodIsHeldBackUntilTheIntervalElapsed()
    {
        long now = 0;
        var throttle = new ReportThrottle(Interval, () => now);

        Assert.True(throttle.TryAcquire(out int first));
        Assert.Equal(0, first);

        now = 5_000;
        Assert.False(throttle.TryAcquire(out _));
        Assert.False(throttle.TryAcquire(out _));

        now = 10_000;
        Assert.True(throttle.TryAcquire(out int suppressed));
        Assert.Equal(2, suppressed);
    }

    [Fact]
    public void AReportAtTickZero_StillCountsAsTheFirst()
    {
        var throttle = new ReportThrottle(Interval, () => 0);

        Assert.True(throttle.TryAcquire(out _));
        Assert.False(throttle.TryAcquire(out _));
    }
}
