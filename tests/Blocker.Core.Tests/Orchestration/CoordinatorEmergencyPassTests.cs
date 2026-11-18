using Blocker.Core.Orchestration;
using Blocker.Sdk.Extensions;
using Blocker.Core.Journal;
using Blocker.Core.Session;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorEmergencyPassTests
{
    private static readonly TimeSpan PassLength = TimeSpan.FromMinutes(5);

    private static string StubArgs(string image, string reason) => $"--blocked \"{image}\" --reason {reason}";

    [Fact]
    public async Task Pass_RollsTheWholeJournalBack_AndSuspendsTheFirewall()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];

        EmergencyPassResult result = await h.Coordinator.UseEmergencyPassAsync(Ct);

        Assert.Equal(EmergencyPassResult.Started, result);
        Assert.Equal(SessionPhase.EmergencyPass, h.Coordinator.Snapshot.Phase);
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.False(h.Firewall.IsEnabled);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Equal(applied.AsEnumerable().Reverse(), h.JournalHarness.System.RestoreLog);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.True(await IsListeningAsync(port)); // the proxy keeps its port for the re-apply
        Assert.Equal(1, h.Monitor.StartCalls);
        Assert.Equal(2, h.Monitor.Handlers);
    }

    [Fact]
    public async Task Pass_RefusedOrRepeated_ChangesNothing()
    {
        await using var none = new OrchestrationHarness();
        Assert.Equal(EmergencyPassResult.NotInSession, await none.Coordinator.UseEmergencyPassAsync(Ct));

        await using var noPasses = new OrchestrationHarness();
        await noPasses.StartAsync(config: Config with { EmergencyPassesPerDay = 0 });
        Assert.Equal(EmergencyPassResult.NoPassesLeft, await noPasses.Coordinator.UseEmergencyPassAsync(Ct));
        Assert.Equal(7, noPasses.JournalHarness.System.Values.Count);
        Assert.True(noPasses.Firewall.IsEnabled);

        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        List<string> restored = [.. h.JournalHarness.System.RestoreLog];
        Assert.Equal(EmergencyPassResult.AlreadyActive, await h.Coordinator.UseEmergencyPassAsync(Ct));
        Assert.Equal(restored, h.JournalHarness.System.RestoreLog);
    }

    [Fact]
    public async Task DuringAPass_NothingIsBlocked_AndPluginsAreNotAsked()
    {
        int consulted = 0;
        await using var h = new OrchestrationHarness(interceptor: (_, _) =>
        {
            Interlocked.Increment(ref consulted);
            return ValueTask.FromResult<IReadOnlyList<InterceptDecision>>([InterceptDecision.Block("no")]);
        });
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);

        await h.SpawnAsync(P(20, Game));
        await h.SpawnAsync(P(21, Cmd));
        await h.SpawnAsync(P(22, Editor));

        Assert.Empty(h.Controller.Terminated);
        Assert.Empty(h.Launcher.Launches);
        Assert.Empty(h.BlockedEvents);
        Assert.Equal(0, consulted);
    }

    [Fact]
    public async Task PassEnd_ReappliesThePlan_ReenablesTheFirewall_AndClosesDisallowedAppsGracefully()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        h.Log.Clear();
        await h.SpawnAsync(P(10, Explorer));
        await h.SpawnAsync(P(12, Editor));
        await h.SpawnAsync(P(13, @"C:\Temp\helper.exe", parent: 12)); // inherits from the allowlisted editor even though started during the pass
        await h.SpawnAsync(P(14, @"C:\Temp\sub.exe", parent: 13));
        await h.SpawnAsync(P(20, Game));
        await h.SpawnAsync(P(31, Cmd));
        Assert.Empty(h.Controller.Terminated);

        h.AdvanceSession(PassLength);
        await h.TickAsync();

        Assert.Equal(SessionPhase.Active, h.Coordinator.Snapshot.Phase);
        Assert.Equal(applied, Names(await h.EntriesAsync()));
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal([.. applied.Select(static t => $"apply:{t}"), "firewall.enable"], h.Log);
        Assert.Single(h.Proxies); // same proxy, same port
        Assert.Equal(port, h.Planner.Inputs[1].ProxyPort);
        Assert.Equal([20, 31], h.Controller.GracefullyClosed.Order());
        Assert.Equal([(20, 20_000L), (31, 31_000L)], h.Controller.Terminated.OrderBy(static t => t.ProcessId));
        Assert.Equal(
            [(StubPath, StubArgs(Game, "NotAllowlisted")), (StubPath, StubArgs(Cmd, "BypassTool"))],
            h.Launcher.Launches.OrderBy(static l => l.Arguments, StringComparer.Ordinal));
        Assert.Equal([BlockReason.NotAllowlisted, BlockReason.BypassTool], h.BlockedEvents.Select(static e => e.Reason).Order());
        Assert.Equal(
            [new EnforcementNotification(EnforcementNotificationKind.PassEnded, "The emergency pass ended; the session is enforced again.")],
            h.Notifications);
        Assert.Single(h.Events.Events.OfType<EmergencyPassEnded>());

        // Back to normal: new violations are blocked as before.
        await h.SpawnAsync(P(40, @"C:\Games\again.exe"));
        Assert.Contains((40, 40_000L), h.Controller.Terminated);
    }

    [Fact]
    public async Task PassEnd_UsesTheAgentCallback_AndTerminatesAfterTheTenSecondBudget()
    {
        TaskCompletionSource<bool> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> asked = [];
        await using var h = new OrchestrationHarness(closeGracefully: (pid, ct) =>
        {
            lock (asked)
            {
                asked.Add(pid);
            }

            return never.Task.WaitAsync(ct);
        });
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        await h.SpawnAsync(P(20, Game));
        h.AdvanceSession(PassLength);
        DateTimeOffset before = h.Time.GetUtcNow();

        Task tick = h.TickAsync().AsTask();
        await h.AdvanceUntilAsync(tick, TimeSpan.FromSeconds(1));

        Assert.Equal([20], asked);
        Assert.Empty(h.Controller.GracefullyClosed); // the callback replaced the controller's close
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.True(h.Time.GetUtcNow() - before >= EnforcementCoordinator.GracefulCloseBudget);
    }

    [Fact]
    public async Task PassEnd_WhenTheAppClosesItself_StillEndsWithATerminateAttempt()
    {
        List<int> asked = [];
        await using var h = new OrchestrationHarness(closeGracefully: (pid, _) =>
        {
            asked.Add(pid);
            return Task.FromResult(true);
        });
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        await h.SpawnAsync(P(20, Game));
        h.AdvanceSession(PassLength);

        await h.TickAsync();

        Assert.Equal([20], asked);
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated); // idempotent: a process that already left simply returns false
    }

    [Fact]
    public async Task Pass_WarningIsRaisedOnce_ThenEndsWithResume()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);

        h.AdvanceSession(TimeSpan.FromSeconds(239));
        await h.TickAsync();
        Assert.Empty(h.Notifications);

        h.AdvanceSession(TimeSpan.FromSeconds(1)); // 60 s left
        await h.TickAsync();
        await h.TickAsync();
        Assert.Equal(
            [new EnforcementNotification(EnforcementNotificationKind.PassEndingSoon, "Your emergency pass ends in less than a minute. Save your work.")],
            h.Notifications);
        Assert.Empty(h.JournalHarness.System.Values);

        h.AdvanceSession(TimeSpan.FromSeconds(60));
        await h.TickAsync();
        Assert.Equal(
            [EnforcementNotificationKind.PassEndingSoon, EnforcementNotificationKind.PassEnded],
            h.Notifications.Select(static n => n.Kind));
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
    }

    [Fact]
    public async Task SessionExpiringDuringAPass_TearsDownWithoutReapplying()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        h.AdvanceSession(TimeSpan.FromHours(1));

        await h.TickAsync();

        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Single(h.Planner.Inputs);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(h.Firewall.IsEnabled);
        Assert.False(await IsListeningAsync(port));
        Assert.Equal([typeof(SessionStarted), typeof(EmergencyPassStarted), typeof(EmergencyPassEnded), typeof(SessionEnded)], h.Events.Events.Select(static e => e.GetType()));
        Assert.Equal([EnforcementNotificationKind.SessionEnded], h.Notifications.Select(static n => n.Kind));
    }

    [Fact]
    public async Task PassEnd_WhenTheFirewallFails_DegradesAndRetriesAfterTheBackoff()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        h.Firewall.EnableFault = new IOException("wfp down");
        h.AdvanceSession(PassLength);

        await h.TickAsync();

        Assert.Equal(SessionPhase.Active, h.Coordinator.Snapshot.Phase);
        Assert.False(h.Firewall.IsEnabled);
        Assert.Equal(
            [
                new EnforcementNotification(EnforcementNotificationKind.PassEnded, "The emergency pass ended; the session is enforced again."),
                new EnforcementNotification(EnforcementNotificationKind.ProtectionDegraded, "Some protections could not be applied and will be retried: wfp down"),
            ],
            h.Notifications);

        // Too early: no retry yet.
        h.Firewall.EnableFault = null;
        h.Time.Advance(TimeSpan.FromSeconds(9));
        await h.TickAsync();
        Assert.False(h.Firewall.IsEnabled);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.TickAsync();

        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.All(await h.EntriesAsync(), static e => Assert.Equal(JournalEntryState.Applied, e.State));
        Assert.Equal(2, h.Notifications.Count); // no second warning, no spam
    }

    [Fact]
    public async Task PassEnd_WhenThePlanFails_StillEnforcesProcesses()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        await h.SpawnAsync(P(20, Game));
        h.Planner.Fault = new InvalidOperationException("planner down");
        h.AdvanceSession(PassLength);

        await h.TickAsync();

        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.Equal(EnforcementNotificationKind.ProtectionDegraded, h.Notifications[^1].Kind);
    }
}
