using Blocker.Core.Orchestration;
using Blocker.Sdk.Events;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

/// <summary>A monitor event without an image path must not decide a process: the system is asked by pid, and what stays unknown is left alone.</summary>
public sealed class CoordinatorUnknownImageTests
{
    private static readonly TimeSpan Interval = EnforcementCoordinator.ReconcileInterval;

    private static async Task TickAfterAsync(OrchestrationHarness h, TimeSpan by)
    {
        h.AdvanceSession(by);
        h.Time.Advance(by);
        await h.TickAsync();
        await h.Coordinator.DrainAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task EmptyImage_IsResolvedByPid_AndAnAllowlistedProgramIsLeftAlone()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Controller.Images[20] = Editor;

        await h.SpawnAsync(P(20, string.Empty));

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
        Assert.Empty(h.BlockedEvents);
    }

    [Fact]
    public async Task EmptyImage_IsResolvedByPid_AndADisallowedProgramIsBlockedUnderItsRealName()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Controller.Images[20] = Game;

        await h.SpawnAsync(P(20, string.Empty));

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        ProcessBlocked blocked = Assert.Single(h.BlockedEvents);
        Assert.Equal("game.exe", blocked.Process.ImageName);
        Assert.Equal(Game, blocked.Process.ExecutablePath);
    }

    [Fact]
    public async Task EmptyImage_ThatCannotBeResolved_IsNeitherTerminatedNorShownTheBlockScreen()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        await h.SpawnAsync(P(20, string.Empty));

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
        Assert.Empty(h.BlockedEvents);
    }

    [Fact]
    public async Task EmptyImage_InSession0_NeedsNoLookup()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Controller.Images[20] = Game; // would be blocked if the lookup ran; session 0 is trusted either way

        await h.SpawnAsync(P(20, string.Empty, session: 0));

        Assert.Empty(h.Controller.Terminated);
    }

    [Fact]
    public async Task Reconciliation_SkipsAnUnknownImage_ThenJudgesItOnceTheSystemCanNameIt()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.SpawnUnreported(P(20, string.Empty));

        await TickAfterAsync(h, Interval);
        Assert.Empty(h.Controller.Terminated);

        h.Controller.Images[20] = Game;
        await TickAfterAsync(h, Interval);

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task EnforcingRunningProcesses_SkipsAnUnknownImage()
    {
        await using var h = new OrchestrationHarness();
        h.Monitor.Running.Add(P(20, string.Empty));

        await h.StartAsync();

        Assert.Empty(h.Controller.Terminated);
    }
}
