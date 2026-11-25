using Blocker.Core.Orchestration;
using Blocker.Core.Ports;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class SharedProcessMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Starting_twice_starts_the_monitor_once_while_it_runs()
    {
        var inner = new FakeProcessMonitor();
        await using var shared = new SharedProcessMonitor(inner);

        await shared.StartAsync(Ct);
        await shared.StartAsync(Ct);

        Assert.Equal(1, inner.StartCalls);
    }

    [Fact]
    public async Task A_monitor_that_stopped_is_restarted_by_the_next_start_and_only_once()
    {
        var inner = new FakeProcessMonitor();
        await using var shared = new SharedProcessMonitor(inner);
        await shared.StartAsync(Ct);
        inner.Stop(new InvalidOperationException("session lost"));

        await Task.WhenAll(shared.StartAsync(Ct), shared.StartAsync(Ct), shared.StartAsync(Ct));

        Assert.Equal(2, inner.StartCalls);
        Assert.False(shared.Completion.IsCompleted);
    }

    [Fact]
    public async Task A_start_that_failed_is_tried_again()
    {
        var inner = new FakeProcessMonitor { StartFault = new InvalidOperationException("no ETW") };
        await using var shared = new SharedProcessMonitor(inner);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () => await shared.StartAsync(Ct));
        inner.StartFault = null;
        await shared.StartAsync(Ct);
        await shared.StartAsync(Ct);

        Assert.Equal(2, inner.StartCalls);
    }

    [Fact]
    public async Task Concurrent_first_starts_reach_the_monitor_once()
    {
        var inner = new FakeProcessMonitor();
        await using var shared = new SharedProcessMonitor(inner);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => shared.StartAsync(Ct)));

        Assert.Equal(1, inner.StartCalls);
    }

    [Fact]
    public async Task Events_snapshots_and_state_pass_through()
    {
        var inner = new FakeProcessMonitor();
        await using var shared = new SharedProcessMonitor(inner);
        List<int> started = [];
        List<int> exited = [];
        void OnStarted(object? sender, ProcessStartedEventArgs e) => started.Add(e.Process.ProcessId);
        void OnExited(object? sender, ProcessExitedEventArgs e) => exited.Add(e.ProcessId);
        shared.ProcessStarted += OnStarted;
        shared.ProcessExited += OnExited;
        ProcessInfo process = P(7, Game);

        inner.Spawn(process);
        Assert.Equal([process], shared.EnumerateRunning());
        inner.Exit(process);
        shared.ProcessStarted -= OnStarted;
        shared.ProcessExited -= OnExited;
        inner.Spawn(P(8, Game));

        Assert.Equal([7], started);
        Assert.Equal([7], exited);
        Assert.Equal(0, inner.Handlers);
        inner.Stop(new InvalidOperationException("x"));
        Assert.Same(inner.Fault, shared.Fault);
    }
}
