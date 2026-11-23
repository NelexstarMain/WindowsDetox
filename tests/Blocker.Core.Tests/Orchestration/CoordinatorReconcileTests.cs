using Blocker.Core.Orchestration;
using Blocker.Core.Ports;
using Blocker.Sdk.Events;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorReconcileTests
{
    private static readonly TimeSpan Interval = EnforcementCoordinator.ReconcileInterval;

    /// <summary>Lets <paramref name="by"/> of fake time pass and runs one tick, then waits for the process pipeline.</summary>
    private static async Task TickAfterAsync(OrchestrationHarness h, TimeSpan by)
    {
        h.AdvanceSession(by);
        h.Time.Advance(by);
        await h.TickAsync();
        await h.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task AProcessTheMonitorNeverReported_IsBlockedWithinOneInterval()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        h.Monitor.SpawnUnreported(P(20, Game));
        await TickAfterAsync(h, Interval);

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.Equal(BlockReason.NotAllowlisted, Assert.Single(h.BlockedEvents).Reason);
    }

    [Fact]
    public async Task Reconciliation_WaitsForItsInterval_AndDoesNotRepeatForKnownProcesses()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.SpawnUnreported(P(20, Game));

        await TickAfterAsync(h, TimeSpan.FromSeconds(1));
        Assert.Empty(h.Controller.Terminated); // the interval has not elapsed yet

        await TickAfterAsync(h, Interval);
        await TickAfterAsync(h, Interval);

        Assert.Single(h.Controller.Terminated); // decided once; the tree knows it afterwards
    }

    [Fact]
    public async Task AllowedAndReportedProcesses_AreLeftAlone_ByReconciliation()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.SpawnAsync(P(10, Editor));
        h.Monitor.SpawnUnreported(P(11, Editor)); // allowlisted, just never reported
        h.Monitor.SpawnUnreported(P(12, Explorer));
        h.Monitor.SpawnUnreported(P(13, @"C:\Temp\helper.exe", parent: 10)); // child of an allowed, live parent

        await TickAfterAsync(h, Interval);

        Assert.Empty(h.Controller.Terminated);
    }

    [Fact]
    public async Task AReconciledChild_DoesNotInheritFromAParentThatAlreadyExited()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        ProcessInfo parent = P(10, Editor);
        await h.SpawnAsync(parent);
        h.Monitor.Exit(parent);
        await h.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        h.Monitor.SpawnUnreported(P(13, @"C:\Temp\helper.exe", parent: 10));

        await TickAfterAsync(h, Interval);

        Assert.Equal([(13, 13_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task NoReconciliation_DuringAnEmergencyPass()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        _ = await h.Coordinator.UseEmergencyPassAsync(Ct);

        h.Monitor.SpawnUnreported(P(20, Game));
        await TickAfterAsync(h, Interval);

        Assert.Empty(h.Controller.Terminated);
    }

    [Fact]
    public async Task AMonitorThatStopped_IsRestarted_AndWhatItMissedIsEnforced()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        Assert.Equal(1, h.Monitor.StartCalls);

        h.Monitor.Stop(new InvalidOperationException("session lost"));
        h.Monitor.SpawnUnreported(P(20, Game)); // started while nobody listened
        await TickAfterAsync(h, Interval);

        Assert.Equal(2, h.Monitor.StartCalls);
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.Equal(2, h.Monitor.Handlers); // the event handlers stay attached across the restart
    }

    [Fact]
    public async Task ARunningMonitor_IsNotRestarted()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await TickAfterAsync(h, Interval);
        await TickAfterAsync(h, Interval);

        Assert.Equal(1, h.Monitor.StartCalls);
    }

    [Fact]
    public async Task AMonitorThatCannotBeRestarted_IsReportedOnce_RetriedEveryInterval_AndStillReconciled()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.Stop();
        h.Monitor.StartFault = new InvalidOperationException("access denied");
        h.Monitor.SpawnUnreported(P(20, Game));

        await TickAfterAsync(h, Interval);
        await TickAfterAsync(h, Interval);

        Assert.Equal(3, h.Monitor.StartCalls); // the original start plus one retry per interval
        Assert.Single(h.Notifications, n => n.Kind == EnforcementNotificationKind.ProtectionDegraded);
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated); // the snapshot still catches it

        h.Monitor.StartFault = null;
        await TickAfterAsync(h, Interval);
        Assert.Equal(4, h.Monitor.StartCalls);
        h.Monitor.SpawnUnreported(P(21, Game));
        await TickAfterAsync(h, Interval);
        Assert.Equal(4, h.Monitor.StartCalls); // running again
        Assert.Contains((21, 21_000L), h.Controller.Terminated);
    }

    [Fact]
    public async Task AFailingEnumeration_DoesNotStopTheTickLoop()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.EnumerateFault = new InvalidOperationException("snapshot failed");

        await TickAfterAsync(h, Interval);
        h.Monitor.EnumerateFault = null;
        h.Monitor.SpawnUnreported(P(20, Game));
        await TickAfterAsync(h, Interval);

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }
}
