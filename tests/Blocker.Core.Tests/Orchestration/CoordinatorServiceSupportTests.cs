using Blocker.Core.Ports;
using Blocker.Core.Session;

namespace Blocker.Core.Tests.Orchestration;

/// <summary>The coordinator members the service host relies on: a refreshable app inventory and blocking the owner of a vetoed window.</summary>
public sealed class CoordinatorServiceSupportTests
{
    private const string NewTool = @"C:\Tools\newtool.exe";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static InstalledApp[] Inventory() =>
    [
        new InstalledApp("New tool", NewTool, null, null, null, AppSource.StartMenu),
        new InstalledApp("Game", OrchestrationHarness.Game, null, null, null, AppSource.StartMenu),
    ];

    [Fact]
    public async Task An_updated_inventory_shapes_the_ifeo_plan_of_the_next_session()
    {
        await using var h = new OrchestrationHarness();

        bool accepted = await h.Coordinator.UpdateScannedAppsAsync(Inventory(), Ct);
        Assert.True((await h.StartAsync()).Started);

        Assert.True(accepted);
        Assert.Contains("newtool.exe", h.Planner.Inputs[0].IfeoImageNames);
        Assert.Contains("game.exe", h.Planner.Inputs[0].IfeoImageNames);
        Assert.DoesNotContain("editor.exe", h.Planner.Inputs[0].IfeoImageNames); // the editor is allowlisted
    }

    [Fact]
    public async Task The_inventory_of_a_running_session_cannot_be_replaced()
    {
        await using var h = new OrchestrationHarness();
        Assert.True((await h.StartAsync()).Started);

        bool accepted = await h.Coordinator.UpdateScannedAppsAsync(Inventory(), Ct);
        await h.RestartAsync();
        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.False(accepted);
        Assert.DoesNotContain("newtool.exe", h.Planner.Inputs[^1].IfeoImageNames);
    }

    [Fact]
    public async Task The_process_of_a_vetoed_window_is_terminated_and_replaced_by_the_block_screen()
    {
        await using var h = new OrchestrationHarness();
        Assert.True((await h.StartAsync()).Started);
        ProcessInfo window = OrchestrationHarness.P(700, OrchestrationHarness.Editor);

        bool blocked = await h.Coordinator.BlockWindowProcessAsync(window.ProcessId, window.StartTimeTicks, window.ImagePath, "Too distracting", Ct);

        Assert.True(blocked);
        Assert.Contains((window.ProcessId, window.StartTimeTicks), h.Controller.Terminated);
        Assert.Contains(h.Launcher.Launches, l => l.Path == OrchestrationHarness.StubPath && l.Arguments.Contains("editor.exe", StringComparison.Ordinal));
        Assert.Contains(h.BlockedEvents, e => e.Process.ProcessId == 700);
    }

    [Theory]
    [InlineData(OrchestrationHarness.Explorer)]
    [InlineData(@"C:\Program Files\Blocker\Blocker.UI.exe")]
    [InlineData("")]
    public async Task Critical_own_and_unknown_images_are_never_blocked_by_a_window_veto(string image)
    {
        await using var h = new OrchestrationHarness();
        Assert.True((await h.StartAsync()).Started);

        bool blocked = await h.Coordinator.BlockWindowProcessAsync(701, 7_010, image, "no", Ct);

        Assert.False(blocked);
        Assert.Empty(h.Controller.Terminated);
    }

    [Fact]
    public async Task A_window_veto_is_ignored_without_a_session_and_during_an_emergency_pass()
    {
        await using var h = new OrchestrationHarness();
        Assert.False(await h.Coordinator.BlockWindowProcessAsync(702, 7_020, OrchestrationHarness.Editor, "no", Ct));
        Assert.True((await h.StartAsync()).Started);
        Assert.Equal(EmergencyPassResult.Started, await h.Coordinator.UseEmergencyPassAsync(Ct));

        bool blocked = await h.Coordinator.BlockWindowProcessAsync(702, 7_020, OrchestrationHarness.Editor, "no", Ct);

        Assert.False(blocked);
        Assert.Empty(h.Controller.Terminated);
    }
}
