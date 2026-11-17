using System.Runtime.CompilerServices;
using Blocker.Core.Resilience;
using Blocker.Sdk;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Tests.Resilience;

public sealed class PluginCircuitBreakerTests
{
    private static readonly PluginId Id = new("com.example.test");
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new();

    private PluginCircuitBreaker Create(CircuitBreakerOptions? options = null) => new(Id, _time, options);

    private static ValueTask<int> Ok(CancellationToken _) => ValueTask.FromResult(1);

    private static ValueTask<int> Throw(CancellationToken _) => throw new InvalidOperationException("boom");

    private async Task Fail(PluginCircuitBreaker breaker) =>
        Assert.Equal(BreakerOutcome.Exception, (await breaker.ExecuteAsync(Throw, -1, Ct)).Outcome);

    [Fact]
    public async Task Success_returns_value()
    {
        var breaker = Create();

        BreakerResult<int> result = await breaker.ExecuteAsync(Ok, -1, Ct);

        Assert.Equal(new BreakerResult<int>(1, BreakerOutcome.Success), result);
        Assert.True(result.Succeeded);
        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.FaultReason);
        Assert.Equal(Id, breaker.PluginId);
    }

    [Fact]
    public async Task Exception_returns_fallback_and_error()
    {
        var breaker = Create();

        BreakerResult<int> result = await breaker.ExecuteAsync(Throw, -1, Ct);

        Assert.Equal(-1, result.Value);
        Assert.Equal(BreakerOutcome.Exception, result.Outcome);
        Assert.Equal("boom", result.Error!.Message);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Synchronous_throw_is_a_failure()
    {
        var breaker = Create();

        BreakerResult<int> result = await breaker.ExecuteAsync(Throw, -1, Ct);

        Assert.Equal(BreakerOutcome.Exception, result.Outcome);
    }

    [Fact]
    public async Task Trips_after_five_consecutive_failures()
    {
        var breaker = Create();
        for (int i = 0; i < 4; i++)
        {
            await Fail(breaker);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);

        await Fail(breaker);

        Assert.Equal(BreakerState.Faulted, breaker.State);
        Assert.Equal("5 consecutive failures (last threw InvalidOperationException).", breaker.FaultReason);
    }

    [Fact]
    public async Task Success_resets_consecutive_count()
    {
        var breaker = Create();
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                await Fail(breaker);
            }

            await breaker.ExecuteAsync(Ok, -1, Ct);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task Trips_after_ten_failures_in_window()
    {
        var breaker = Create();
        for (int i = 0; i < 9; i++)
        {
            await Fail(breaker);
            await breaker.ExecuteAsync(Ok, -1, Ct);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(BreakerState.Closed, breaker.State);

        await Fail(breaker);

        Assert.Equal(BreakerState.Faulted, breaker.State);
        Assert.Equal("10 failures within 60 s (last threw InvalidOperationException).", breaker.FaultReason);
    }

    [Fact]
    public async Task Window_forgets_old_failures()
    {
        var breaker = Create();
        for (int i = 0; i < 9; i++)
        {
            await Fail(breaker);
            await breaker.ExecuteAsync(Ok, -1, Ct);
        }

        _time.Advance(TimeSpan.FromSeconds(60));
        await Fail(breaker);

        Assert.Equal(BreakerState.Closed, breaker.State);

        for (int i = 0; i < 8; i++)
        {
            await Fail(breaker);
            await breaker.ExecuteAsync(Ok, -1, Ct);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);

        await Fail(breaker);

        Assert.Equal(BreakerState.Faulted, breaker.State);
    }

    [Fact]
    public async Task Timeout_is_counted_and_cancels_the_call_token()
    {
        var breaker = Create();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;

        ValueTask<BreakerResult<int>> pending = breaker.ExecuteAsync(
            token =>
            {
                seen = token;
                return new ValueTask<int>(never.Task);
            },
            -1,
            Ct);
        Assert.False(pending.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(49));
        Assert.False(pending.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        BreakerResult<int> result = await pending;

        Assert.Equal(-1, result.Value);
        Assert.Equal(BreakerOutcome.Timeout, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task Five_timeouts_trip_the_breaker()
    {
        var breaker = Create();
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i < 5; i++)
        {
            ValueTask<BreakerResult<int>> pending = breaker.ExecuteAsync(_ => new ValueTask<int>(never.Task), -1, Ct);
            _time.Advance(TimeSpan.FromMilliseconds(50));
            Assert.Equal(BreakerOutcome.Timeout, (await pending).Outcome);
        }

        Assert.Equal(BreakerState.Faulted, breaker.State);
        Assert.Equal("5 consecutive failures (last timed out).", breaker.FaultReason);
    }

    [Fact]
    public async Task Custom_budget_is_honored()
    {
        var breaker = Create(new CircuitBreakerOptions { Budget = TimeSpan.FromSeconds(2) });
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<BreakerResult<int>> pending = breaker.ExecuteAsync(_ => new ValueTask<int>(never.Task), -1, Ct);
        _time.Advance(TimeSpan.FromMilliseconds(1999));
        Assert.False(pending.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(BreakerOutcome.Timeout, (await pending).Outcome);
    }

    [Fact]
    public async Task Faulted_breaker_rejects_without_invoking()
    {
        var breaker = Create();
        for (int i = 0; i < 5; i++)
        {
            await Fail(breaker);
        }

        int calls = 0;
        BreakerResult<int> result = await breaker.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromResult(1);
            },
            -1,
            Ct);

        Assert.Equal(new BreakerResult<int>(-1, BreakerOutcome.Rejected), result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Reset_closes_and_forgets_failures()
    {
        var breaker = Create();
        var changes = new List<(BreakerState State, string? Reason)>();
        breaker.StateChanged += (_, e) => changes.Add((e.State, e.Reason));
        for (int i = 0; i < 5; i++)
        {
            await Fail(breaker);
        }

        breaker.Reset();
        breaker.Reset();

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Null(breaker.FaultReason);
        Assert.Equal(1, (await breaker.ExecuteAsync(Ok, -1, Ct)).Value);
        for (int i = 0; i < 4; i++)
        {
            await Fail(breaker);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);
        Assert.Equal(
            [(BreakerState.Faulted, "5 consecutive failures (last threw InvalidOperationException)."), (BreakerState.Closed, null)],
            changes);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_is_not_a_failure()
    {
        var breaker = Create();
        for (int i = 0; i < 10; i++)
        {
            using var cts = new CancellationTokenSource();
            var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask<BreakerResult<int>> pending = breaker.ExecuteAsync(_ => new ValueTask<int>(never.Task), -1, cts.Token);

            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        }

        Assert.Equal(BreakerState.Closed, breaker.State);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_without_invoking()
    {
        var breaker = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await breaker.ExecuteAsync(
            _ =>
            {
                calls++;
                return ValueTask.FromResult(1);
            },
            -1,
            cts.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Non_generic_overload_reports_success_and_failure()
    {
        var breaker = Create();
        int ran = 0;

        BreakerResult<bool> ok = await breaker.ExecuteAsync(
            _ =>
            {
                ran++;
                return ValueTask.CompletedTask;
            },
            Ct);
        BreakerResult<bool> bad = await breaker.ExecuteAsync(_ => throw new InvalidOperationException("x"), Ct);

        Assert.Equal(new BreakerResult<bool>(true, BreakerOutcome.Success), ok);
        Assert.Equal(1, ran);
        Assert.False(bad.Value);
        Assert.Equal(BreakerOutcome.Exception, bad.Outcome);
    }

    [Fact]
    public async Task Concurrent_failures_trip_exactly_once()
    {
        var breaker = Create();
        int faulted = 0;
        breaker.StateChanged += (_, e) =>
        {
            if (e.State == BreakerState.Faulted)
            {
                Interlocked.Increment(ref faulted);
            }
        };

        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () => await breaker.ExecuteAsync(Throw, -1, Ct), Ct)));

        Assert.Equal(BreakerState.Faulted, breaker.State);
        Assert.Equal(1, faulted);
    }

    [Fact]
    public async Task Abandoned_task_exception_is_observed()
    {
        var unobserved = new List<Exception>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => unobserved.AddRange(e.Exception.InnerExceptions);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await TimeOutThenFault(Create(), _time);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.DoesNotContain(unobserved, ex => ex.Message == "late failure");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task TimeOutThenFault(PluginCircuitBreaker breaker, FakeTimeProvider time)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<BreakerResult<int>> pending = breaker.ExecuteAsync(_ => new ValueTask<int>(tcs.Task), -1, Ct);
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(BreakerOutcome.Timeout, (await pending).Outcome);
        tcs.SetException(new InvalidOperationException("late failure"));
    }
}
