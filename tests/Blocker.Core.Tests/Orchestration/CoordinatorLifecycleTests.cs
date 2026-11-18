using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.WebFilter;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorLifecycleTests
{
    private static readonly string[] PlannedPrefix = ["ifeo:cmd.exe", "ifeo:game.exe", "ifeo:taskmgr.exe"];

    private static string[] Planned(int port) =>
        [.. PlannedPrefix, $"pac:{port}", "service:Blocker.Service", "service:Blocker.Watchdog", $"dir:{InstallDir}"];

    [Fact]
    public async Task Start_AppliesPlanThroughTheJournalInOrder_ThenEnablesFirewall()
    {
        await using var h = new OrchestrationHarness();

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.True(result.Started);
        Assert.NotNull(result.SessionId);
        Assert.Empty(result.Errors);
        Assert.Equal(23, result.BreakGlassCode!.Length);
        int port = h.Proxies.Single().Port;
        Assert.NotEqual(0, port);
        var entries = await h.EntriesAsync();
        Assert.Equal(Planned(port), Names(entries));
        Assert.All(entries, e => Assert.Equal(("fake", JournalEntryState.Applied), (e.Kind, e.State)));
        Assert.Equal([.. Planned(port).Select(static t => $"apply:{t}"), "firewall.enable"], h.Log);
        Assert.Equal([Chrome, Edge], Assert.Single(h.Firewall.EnabledWith));
        SessionPlanInput input = Assert.Single(h.Planner.Inputs);
        Assert.Equal(new SessionPlanInput(port, ["cmd.exe", "game.exe", "taskmgr.exe"], StubPath, [InstallDir], ["Blocker.Service", "Blocker.Watchdog"]), input, new PlanInputComparer());
        Assert.True(await IsListeningAsync(port));
        Assert.Equal(SessionPhase.Active, h.Coordinator.Snapshot.Phase);
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(1, h.Monitor.StartCalls);
        Assert.Equal(2, h.Monitor.Handlers);
        SessionPolicy stored = (await h.Policies.LoadAsync(Ct))!;
        Assert.Equal(DefaultAllowlist.Entries, stored.Entries);
        Assert.Equal(["example.com", "*.docs.example.com"], stored.Domains);
        Assert.IsType<SessionStarted>(h.Events.Events[0]);
    }

    [Fact]
    public async Task Start_ProxyFiltersByTheGivenDomains()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync(["example.com", "*.docs.example.com"]);

        IWebFilterPolicy policy = Assert.Single(h.ProxyPolicies);

        Assert.True(policy.IsAllowed("example.com"));
        Assert.True(policy.IsAllowed("a.b.docs.example.com"));
        Assert.True(policy.IsAllowed("docs.example.com"));
        Assert.False(policy.IsAllowed("www.example.com"));
        Assert.False(policy.IsAllowed("evil.com"));
    }

    [Fact]
    public async Task Start_WithInvalidDomain_IsRefusedBeforeAnythingHappens()
    {
        await using var h = new OrchestrationHarness();

        StartEnforcedSessionResult result = await h.StartAsync(["example.com", "bad domain", "*.com"]);

        Assert.Equal(new StartEnforcedSessionResult(false, null, null, ["Invalid domain pattern 'bad domain'.", "Invalid domain pattern '*.com'."]), result, new ResultComparer());
        Assert.Equal(SessionPhase.Idle, h.Coordinator.Snapshot.Phase);
        Assert.Empty(h.Events.Events);
        Assert.Empty(h.Planner.Inputs);
        Assert.Empty(h.Proxies);
    }

    [Fact]
    public async Task Start_WithInvalidConfig_ReportsTheStateMachineErrors()
    {
        await using var h = new OrchestrationHarness();

        StartEnforcedSessionResult result = await h.StartAsync(config: Config with { Duration = TimeSpan.FromMinutes(1) });

        Assert.False(result.Started);
        Assert.Equal(["Duration must be between 15 minutes and 30 days."], result.Errors);
        Assert.Empty(h.Planner.Inputs);
        Assert.Empty(h.Proxies);
    }

    [Fact]
    public async Task Start_WhileRunning_IsRefusedAndLeavesTheSessionAlone()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        StartEnforcedSessionResult second = await h.StartAsync();

        Assert.Equal(new StartEnforcedSessionResult(false, null, null, ["A session is already running."]), second, new ResultComparer());
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Single(h.Planner.Inputs);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
    }

    [Fact]
    public async Task Start_ApplyFailsMidPlan_RollsBackEverythingAndReports()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t.StartsWith("pac:", StringComparison.Ordinal) ? new IOException("registry denied") : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        int port = h.Proxies.Single().Port;
        Assert.Equal(new StartEnforcedSessionResult(false, null, null, ["The session could not be enforced and was rolled back: registry denied"]), result, new ResultComparer());
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Equal([$"pac:{port}", "ifeo:taskmgr.exe", "ifeo:game.exe", "ifeo:cmd.exe"], h.JournalHarness.System.RestoreLog);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.False(h.Firewall.IsEnabled);
        Assert.DoesNotContain("firewall.enable", h.Log);
        Assert.False(await IsListeningAsync(port));
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Equal([typeof(SessionStarted), typeof(SessionEnded)], h.Events.Events.Select(static e => e.GetType()));
        Assert.Equal(SessionEndReason.BreakGlass, h.Events.Events.OfType<SessionEnded>().Single().Reason);
        Assert.Null(await h.Sessions.LoadAsync(Ct));
        Assert.Null(await h.Policies.LoadAsync(Ct));
        Assert.Equal(0, h.Monitor.Handlers);
        Assert.Empty(h.Notifications);

        // Nothing is stuck: after fixing the cause the next start succeeds.
        h.JournalHarness.System.ApplyFault = null;
        Assert.True((await h.StartAsync()).Started);
        Assert.Equal(2, h.Proxies.Count);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
    }

    [Fact]
    public async Task Start_PlannerFailure_RollsBackWithoutApplyingAnything()
    {
        await using var h = new OrchestrationHarness();
        h.Planner.Fault = new InvalidOperationException("no plan");

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.Equal(["The session could not be enforced and was rolled back: no plan"], result.Errors);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Empty(h.JournalHarness.System.RestoreLog);
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.False(await IsListeningAsync(h.Proxies.Single().Port));
    }

    [Fact]
    public async Task Start_FirewallFailure_RollsBackTheAppliedPlan()
    {
        await using var h = new OrchestrationHarness();
        h.Firewall.EnableFault = new InvalidOperationException("wfp unavailable");

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.Equal(["The session could not be enforced and was rolled back: wfp unavailable"], result.Errors);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Equal(7, h.JournalHarness.System.RestoreLog.Count);
        Assert.False(h.Firewall.IsEnabled);
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Null(await h.Sessions.LoadAsync(Ct));
    }

    [Fact]
    public async Task Start_MonitorFailure_RollsBack()
    {
        await using var h = new OrchestrationHarness();
        h.Monitor.StartFault = new InvalidOperationException("etw busy");

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.Equal(["The session could not be enforced and was rolled back: etw busy"], result.Errors);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Equal(0, h.Monitor.Handlers);
        Assert.False(h.Coordinator.IsSessionActive);
    }

    [Fact]
    public async Task Start_ProxyPortInUse_RollsBack()
    {
        await using var first = new OrchestrationHarness();
        await first.StartAsync();
        int taken = first.Proxies.Single().Port;
        await using var h = new OrchestrationHarness(settings: Settings with { ProxyPort = taken });

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.False(result.Started);
        Assert.StartsWith("The session could not be enforced and was rolled back: ", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Null(await h.Sessions.LoadAsync(Ct));
    }

    [Fact]
    public async Task Start_EnumerationFailure_DoesNotPreventTheSession()
    {
        await using var h = new OrchestrationHarness();
        h.Monitor.EnumerateFault = new InvalidOperationException("snapshot failed");

        Assert.True((await h.StartAsync()).Started);
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
    }

    [Fact]
    public async Task Expiry_TearsEverythingDown()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];
        h.Log.Clear();

        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();

        await AssertTornDownAsync(h, port, applied);
        Assert.Equal(SessionEndReason.Expired, h.Events.Events.OfType<SessionEnded>().Single().Reason);
        Assert.Equal([new EnforcementNotification(EnforcementNotificationKind.SessionEnded, "The session ended and all restrictions were removed.")], h.Notifications);
        Assert.Equal(["firewall.disable"], h.Log);

        // Teardown is idempotent: nothing is left to do, nothing throws.
        await h.TickAsync();
        Assert.False(await h.Coordinator.BreakGlassAsync("AAAAA-AAAAA-AAAAA-AAAAA", Ct));
        Assert.Single(h.Notifications);
    }

    [Fact]
    public async Task BreakGlass_WrongCode_ChangesNothing()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();

        Assert.False(await h.Coordinator.BreakGlassAsync("AAAAA-AAAAA-AAAAA-AAAAA", Ct));

        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.True(h.Firewall.IsEnabled);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task BreakGlass_RightCode_TearsEverythingDown_AndANewSessionCanStart()
    {
        await using var h = new OrchestrationHarness();
        StartEnforcedSessionResult started = await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];

        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));

        await AssertTornDownAsync(h, port, applied);
        Assert.Equal(SessionEndReason.BreakGlass, h.Events.Events.OfType<SessionEnded>().Single().Reason);
        Assert.Equal(EnforcementNotificationKind.SessionEnded, Assert.Single(h.Notifications).Kind);

        StartEnforcedSessionResult again = await h.StartAsync();
        Assert.True(again.Started);
        Assert.NotEqual(started.SessionId, again.SessionId);
        Assert.Equal(2, h.Proxies.Count);
        Assert.True(await IsListeningAsync(h.Proxies[1].Port));
    }

    [Fact]
    public async Task DeveloperAbort_WhenEnabled_TearsDown_OtherwiseRefuses()
    {
        await using var locked = new OrchestrationHarness();
        await locked.StartAsync();
        Assert.False(await locked.Coordinator.DeveloperAbortAsync(Ct));
        Assert.True(locked.Coordinator.IsSessionActive);
        Assert.Equal(7, locked.JournalHarness.System.Values.Count);

        await using var dev = new OrchestrationHarness(allowAbort: true);
        await dev.StartAsync();
        int port = dev.Proxies.Single().Port;
        List<string> applied = [.. Names(await dev.EntriesAsync())];

        Assert.True(await dev.Coordinator.DeveloperAbortAsync(Ct));

        await AssertTornDownAsync(dev, port, applied);
        Assert.Equal(SessionEndReason.DeveloperAbort, dev.Events.Events.OfType<SessionEnded>().Single().Reason);
    }

    [Fact]
    public async Task Teardown_SurvivesFailingPorts_AndStillRollsBack()
    {
        await using var h = new OrchestrationHarness();
        StartEnforcedSessionResult started = await h.StartAsync();
        h.Firewall.DisableFault = new InvalidOperationException("wfp gone");

        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));

        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.False(await IsListeningAsync(h.Proxies.Single().Port));
        Assert.Null(await h.Policies.LoadAsync(Ct));
    }

    [Fact]
    public async Task Teardown_IncompleteRollback_NotifiesAndKeepsTheJournalForTheNextStart()
    {
        await using var h = new OrchestrationHarness();
        StartEnforcedSessionResult started = await h.StartAsync();
        h.JournalHarness.System.RestoreFault = static t => t == "ifeo:game.exe" ? new IOException("locked") : null;

        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));

        Assert.True(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.Equal(["ifeo:game.exe"], h.JournalHarness.System.Values.Keys);
        Assert.Equal(
            [EnforcementNotificationKind.ProtectionDegraded, EnforcementNotificationKind.SessionEnded],
            h.Notifications.Select(static n => n.Kind));

        h.JournalHarness.System.RestoreFault = null;
        await h.RestartAsync();
        await h.Coordinator.RecoverOnStartupAsync(Ct);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task Notification_HandlerExceptionsDoNotBreakTeardownOrOtherHandlers()
    {
        await using var h = new OrchestrationHarness();
        List<EnforcementNotification> later = [];
        h.Coordinator.Notification += static (_, _) => throw new InvalidOperationException("toast failed");
        h.Coordinator.Notification += (_, n) => later.Add(n);
        await h.StartAsync();

        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();

        Assert.Equal(EnforcementNotificationKind.SessionEnded, Assert.Single(later).Kind);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(h.Coordinator.IsSessionActive);
    }

    [Fact]
    public async Task TickLoop_RunsOnTheTimer_AndExpiresTheSession()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];

        h.AdvanceSession(TimeSpan.FromHours(1));
        h.Time.Advance(EnforcementCoordinator.TickInterval);
        await h.Coordinator.WaitForTicksAsync(1, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await AssertTornDownAsync(h, port, applied);
        Assert.Equal(SessionEndReason.Expired, h.Events.Events.OfType<SessionEnded>().Single().Reason);
    }

    [Fact]
    public async Task TickLoop_SurvivesAThrowingPort_AndKeepsTicking()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;
        List<string> applied = [.. Names(await h.EntriesAsync())];

        h.Sessions.SaveFault = new IOException("disk full");
        h.AdvanceSession(TimeSpan.FromSeconds(5));
        h.Time.Advance(EnforcementCoordinator.TickInterval);
        await h.Coordinator.WaitForTicksAsync(1, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);

        h.Sessions.SaveFault = null;
        h.AdvanceSession(TimeSpan.FromSeconds(5));
        h.Time.Advance(EnforcementCoordinator.TickInterval);
        await h.Coordinator.WaitForTicksAsync(2, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.True(h.Coordinator.IsSessionActive);

        h.AdvanceSession(TimeSpan.FromHours(1));
        h.Time.Advance(EnforcementCoordinator.TickInterval);
        await h.Coordinator.WaitForTicksAsync(3, Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await AssertTornDownAsync(h, port, applied);
    }

    [Fact]
    public async Task Dispose_StopsActivityButKeepsTheSessionAndTheJournal()
    {
        var h = new OrchestrationHarness();
        await h.StartAsync();
        int port = h.Proxies.Single().Port;

        await h.Coordinator.DisposeAsync();

        Assert.False(await IsListeningAsync(port));
        Assert.Equal(0, h.Monitor.Handlers);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.True(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.NotNull(await h.Sessions.LoadAsync(Ct));
        Assert.NotNull(await h.Policies.LoadAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await h.Coordinator.UseEmergencyPassAsync(Ct));
        await h.Coordinator.DisposeAsync(); // idempotent
    }

    private static async Task AssertTornDownAsync(OrchestrationHarness h, int port, IReadOnlyList<string> applied)
    {
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Equal(SessionPhase.Idle, h.Coordinator.Snapshot.Phase);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Empty(h.JournalHarness.System.Owners);
        Assert.Equal(applied.AsEnumerable().Reverse(), h.JournalHarness.System.RestoreLog);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.False(h.Firewall.IsEnabled);
        Assert.False(await IsListeningAsync(port));
        Assert.Equal(0, h.Monitor.Handlers);
        Assert.Null(await h.Policies.LoadAsync(Ct));
        Assert.Null(await h.Sessions.LoadAsync(Ct));
    }

    private sealed class PlanInputComparer : IEqualityComparer<SessionPlanInput>
    {
        public bool Equals(SessionPlanInput? x, SessionPlanInput? y) =>
            x is not null && y is not null
            && x.ProxyPort == y.ProxyPort
            && x.StubExecutablePath == y.StubExecutablePath
            && x.IfeoImageNames.SequenceEqual(y.IfeoImageNames)
            && x.ProtectedDirectories.SequenceEqual(y.ProtectedDirectories)
            && x.ServiceNames.SequenceEqual(y.ServiceNames);

        public int GetHashCode(SessionPlanInput obj) => obj.ProxyPort;
    }

    private sealed class ResultComparer : IEqualityComparer<StartEnforcedSessionResult>
    {
        public bool Equals(StartEnforcedSessionResult? x, StartEnforcedSessionResult? y) =>
            x is not null && y is not null
            && x.Started == y.Started
            && x.SessionId == y.SessionId
            && x.BreakGlassCode == y.BreakGlassCode
            && x.Errors.SequenceEqual(y.Errors);

        public int GetHashCode(StartEnforcedSessionResult obj) => obj.Started.GetHashCode();
    }
}
