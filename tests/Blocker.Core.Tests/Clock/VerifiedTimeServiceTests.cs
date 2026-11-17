using Blocker.Core.Clock;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Clock;

public sealed class VerifiedTimeServiceTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeMonotonicClock _clock = new(10_000);
    private readonly FakeTimeProvider _timeouts = new();

    private VerifiedTimeService Service(params INetworkTimeSource[] sources) => new(sources, _clock, _timeouts);

    private static FakeTimeSource At(string name, TimeSpan offset) => new(name, T + offset);

    [Fact]
    public void NoAnchorInitially()
    {
        var service = Service(At("a", TimeSpan.Zero));

        Assert.False(service.HasAnchor);
        Assert.False(service.TryGetUtcNow(out var utc));
        Assert.Equal(default, utc);
    }

    [Fact]
    public async Task ThreeAgreeingSources_AnchorToMedianAndAdvanceWithMonotonicClock()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromMilliseconds(500)), At("c", TimeSpan.FromSeconds(1)));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.HasAnchor);
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMilliseconds(500), now);

        _clock.Advance(TimeSpan.FromSeconds(90));

        Assert.True(service.TryGetUtcNow(out now));
        Assert.Equal(T.AddMilliseconds(500).AddSeconds(90), now);
    }

    [Fact]
    public async Task TwoAgreeingSources_AreEnough_AnchorIsTheirMidpoint()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromSeconds(1)));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMilliseconds(500), now);
    }

    [Fact]
    public async Task OutlierIsIgnored_WhenTwoOthersAgree()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromSeconds(1)), At("liar", TimeSpan.FromHours(1)));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMilliseconds(500), now);
    }

    [Fact]
    public async Task ExactlyTwoSecondsFromMedian_StillAgrees()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromSeconds(4)));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddSeconds(2), now);
    }

    [Fact]
    public async Task TwoDisagreeingSources_NoAnchor()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromSeconds(10)));

        Assert.False(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(service.HasAnchor);
        Assert.False(service.TryGetUtcNow(out _));
    }

    [Fact]
    public async Task ThreeMutuallyDisagreeingSources_NoAnchor()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.FromSeconds(3)), At("c", TimeSpan.FromSeconds(6)));

        Assert.False(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(service.HasAnchor);
    }

    [Fact]
    public async Task SingleSource_NoAnchor()
    {
        Assert.False(await Service(At("a", TimeSpan.Zero)).RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OnlyOneOfThreeResponds_NoAnchor()
    {
        var service = Service(At("a", TimeSpan.Zero), new FakeTimeSource("b", (DateTimeOffset?)null), new FakeTimeSource("c", (DateTimeOffset?)null));

        Assert.False(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.False(service.HasAnchor);
    }

    [Fact]
    public async Task NoSources_NoAnchor()
    {
        Assert.False(await Service().RefreshAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedRefresh_KeepsPreviousAnchor()
    {
        var offset = TimeSpan.Zero;
        var service = Service(
            new FakeTimeSource("a", _ => ValueTask.FromResult<DateTimeOffset?>(T)),
            new FakeTimeSource("b", _ => ValueTask.FromResult<DateTimeOffset?>(T + offset)));
        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromMinutes(5));

        offset = TimeSpan.FromMinutes(30);

        Assert.False(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMinutes(5), now);
    }

    [Fact]
    public async Task SuccessfulRefresh_ReplacesAnchor()
    {
        var service = Service(At("a", TimeSpan.FromMinutes(10)), At("b", TimeSpan.FromMinutes(10)));
        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMinutes(10), now);
    }

    [Fact]
    public async Task FaultySource_CountsAsMissingVote()
    {
        var service = Service(
            At("a", TimeSpan.Zero),
            new FakeTimeSource("boom", _ => throw new InvalidOperationException("bad source")),
            At("c", TimeSpan.FromMilliseconds(200)));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddMilliseconds(100), now);
    }

    [Fact]
    public async Task SlowSource_IsTimedOutAfterFiveSeconds_WhileOthersStillCount()
    {
        var hung = new TaskCompletionSource<DateTimeOffset?>();
        var service = Service(At("a", TimeSpan.Zero), new FakeTimeSource("hung", _ => new ValueTask<DateTimeOffset?>(hung.Task)), At("c", TimeSpan.Zero));

        var refresh = service.RefreshAsync(TestContext.Current.CancellationToken);
        _timeouts.Advance(TimeSpan.FromSeconds(4.9));
        Assert.False(refresh.IsCompleted);

        _timeouts.Advance(TimeSpan.FromSeconds(0.2));

        Assert.True(await refresh);
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T, now);
    }

    [Fact]
    public async Task SlowSource_TimeoutLeavesTooFewResults_NoAnchor()
    {
        var hung = new TaskCompletionSource<DateTimeOffset?>();
        var service = Service(At("a", TimeSpan.Zero), new FakeTimeSource("hung", _ => new ValueTask<DateTimeOffset?>(hung.Task)));

        var refresh = service.RefreshAsync(TestContext.Current.CancellationToken);
        _timeouts.Advance(VerifiedTimeService.SourceTimeout);

        Assert.False(await refresh);
        Assert.False(service.HasAnchor);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        var hung = new TaskCompletionSource<DateTimeOffset?>();
        var service = Service(new FakeTimeSource("hung", _ => new ValueTask<DateTimeOffset?>(hung.Task)));
        using var cts = new CancellationTokenSource();

        var refresh = service.RefreshAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task AnswersAreAlignedToACommonInstant_BeforeComparing()
    {
        // "slow" answers 3 s (monotonic) later with a time that is 3 s ahead: consistent, so the sources agree.
        var service = Service(
            new FakeTimeSource("fast", _ => ValueTask.FromResult<DateTimeOffset?>(T)),
            new FakeTimeSource(
                "slow",
                _ =>
                {
                    _clock.Advance(TimeSpan.FromSeconds(3));
                    return ValueTask.FromResult<DateTimeOffset?>(T.AddSeconds(3));
                }));

        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.True(service.TryGetUtcNow(out var now));
        Assert.Equal(T.AddSeconds(3), now);
    }

    [Fact]
    public async Task TickCounterGoingBackwards_InvalidatesAnchor()
    {
        var service = Service(At("a", TimeSpan.Zero), At("b", TimeSpan.Zero));
        Assert.True(await service.RefreshAsync(TestContext.Current.CancellationToken));

        _clock.TickCount64 = 5; // reboot

        Assert.False(service.TryGetUtcNow(out _));
    }
}
