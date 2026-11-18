using Blocker.Core.Orchestration;
using Blocker.Core.Tests.Journal;
using Blocker.Core.Journal;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorRecoveryTests
{
    [Fact]
    public async Task ActiveSession_RollsBackAndReappliesDeterministically_AndResumesEnforcement()
    {
        await using var h = new OrchestrationHarness();
        StartEnforcedSessionResult started = await h.StartAsync();
        int oldPort = h.Proxies.Single().Port;
        List<string> original = [.. Names(await h.EntriesAsync())];
        h.Monitor.Running.AddRange([P(12, Editor), P(20, Game)]);
        h.JournalHarness.System.RestoreLog.Clear();
        h.Log.Clear();

        await h.RestartAsync();
        // The service went down: the protections and the persisted session are still there, the proxy is not.
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.False(await IsListeningAsync(oldPort));
        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(SessionPhase.Active, h.Coordinator.Snapshot.Phase);
        Assert.Equal(original.AsEnumerable().Reverse(), h.JournalHarness.System.RestoreLog); // rolled back first, in reverse
        int newPort = h.Proxies[1].Port;
        string[] expected = [.. original.Select(t => t == $"pac:{oldPort}" ? $"pac:{newPort}" : t)];
        var entries = await h.EntriesAsync();
        Assert.Equal(expected, Names(entries));
        Assert.All(entries, static e => Assert.Equal(JournalEntryState.Applied, e.State));
        Assert.Equal([.. expected.Select(static t => $"apply:{t}"), "firewall.enable"], h.Log);
        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal(newPort, h.Planner.Inputs[1].ProxyPort);
        Assert.True(await IsListeningAsync(newPort));
        Assert.Equal(2, h.Monitor.StartCalls);
        Assert.Equal(2, h.Monitor.Handlers);

        // Enforcement resumed: the snapshot was enforced and live events are handled.
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        await h.SpawnAsync(P(30, @"C:\Games\x.exe"));
        Assert.Equal([(20, 20_000L), (30, 30_000L)], h.Controller.Terminated);

        // The restored session still honours its break-glass code and the saved allowlist.
        await h.SpawnAsync(P(31, @"C:\Temp\kid.exe", parent: 12));
        Assert.Equal(2, h.Controller.Terminated.Count);
        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));
        Assert.Empty(h.JournalHarness.System.Values);
    }

    [Fact]
    public async Task NoSession_WithPendingJournal_RollsBack()
    {
        await using var h = new OrchestrationHarness();
        await h.Journal.ApplyAsync("fake", "orphan:1", "{}", Ct);
        await h.Journal.ApplyAsync("fake", "orphan:2", "{}", Ct);
        Assert.True(await h.Journal.HasPendingChangesAsync(Ct));
        h.Log.Clear();

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Equal(["orphan:2", "orphan:1"], h.JournalHarness.System.RestoreLog);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Equal(0, h.Monitor.StartCalls);
        Assert.Empty(h.Proxies);
        Assert.Empty(h.Log);
    }

    [Fact]
    public async Task NoSession_WithDamagedJournal_SweepsByMarker()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.Values["leftover"] = "{}";
        h.JournalHarness.System.Owners["leftover"] = JournalHarness.InstallId;
        h.JournalHarness.System.Values["foreign"] = "{}";
        h.Fs.File.WriteAllBytes(h.JournalHarness.JournalPath, [1, 2, 3, 4]);

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.Equal(["foreign"], h.JournalHarness.System.Values.Keys);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.False(h.Coordinator.IsSessionActive);
    }

    [Fact]
    public async Task NoSession_AndNothingPending_DoesNothing()
    {
        await using var h = new OrchestrationHarness();

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.Empty(h.JournalHarness.System.RestoreLog);
        Assert.Empty(h.Log);
        Assert.Equal(0, h.Monitor.StartCalls);
        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Empty(h.Events.Events);
    }

    [Fact]
    public async Task ActiveSession_WithDamagedJournal_KeepsTheSessionAndReappliesInsteadOfEndingIt()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.Running.Add(P(20, Game));
        h.JournalHarness.System.RestoreLog.Clear();
        await h.RestartAsync();
        h.Fs.File.WriteAllBytes(h.JournalHarness.JournalPath, [9, 9, 9, 9]);
        Assert.True(await h.Journal.HasPendingChangesAsync(Ct)); // damaged counts as pending

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.True(h.Coordinator.IsSessionActive);
        Assert.NotNull(await h.Sessions.LoadAsync(Ct));
        Assert.Empty(h.Events.Events.OfType<SessionEnded>());
        Assert.Empty(h.JournalHarness.System.RestoreLog); // no journal replay happened
        int port = h.Proxies[1].Port;
        string[] expected = [.. Planned(port)];
        Assert.Equal(expected, h.JournalHarness.System.Values.Keys.Order(StringComparer.Ordinal));
        var entries = await h.EntriesAsync(); // the journal is valid again
        Assert.Equal(expected.Length, entries.Count);
        Assert.All(entries, static e => Assert.Equal(JournalEntryState.Applied, e.State));
        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task ActiveSession_WhoseSweepFails_StaysActiveAndRetriesLater()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.RestartAsync();
        h.Fs.File.WriteAllBytes(h.JournalHarness.JournalPath, [9, 9, 9, 9]);
        h.JournalHarness.System.SweepFault = static () => new IOException("registry locked");

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(EnforcementNotificationKind.ProtectionDegraded, Assert.Single(h.Notifications).Kind);
        Assert.False(h.Firewall.IsEnabled);

        h.JournalHarness.System.SweepFault = null;
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await h.TickAsync();

        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal(7, (await h.EntriesAsync()).Count);
    }

    [Fact]
    public async Task ActiveSession_WithoutASavedPolicy_FailsClosedAndSaysSo()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.Monitor.Running.Add(P(12, Editor));
        await h.Policies.ClearAsync(Ct);
        await h.RestartAsync();

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.True(h.Coordinator.IsSessionActive);
        Assert.Equal(
            [new EnforcementNotification(EnforcementNotificationKind.ProtectionDegraded, "The saved allowlist is unavailable; everything outside the system baseline is blocked until the session ends.")],
            h.Notifications);
        Assert.Equal([(12, 12_000L)], h.Controller.Terminated); // the editor is no longer allowlisted
        Assert.Equal(["cmd.exe", "editor.exe", "game.exe", "taskmgr.exe"], h.Planner.Inputs[1].IfeoImageNames);
    }

    [Fact]
    public async Task SessionExpiredWhileDown_IsEndedAndRolledBack()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.RestartAsync();
        h.AdvanceSession(TimeSpan.FromHours(2));

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
        Assert.Equal(SessionEndReason.Expired, h.Events.Events.OfType<SessionEnded>().Single().Reason);
        Assert.Null(await h.Policies.LoadAsync(Ct));
        Assert.Single(h.Proxies); // the second coordinator started nothing
        Assert.Equal(1, h.Monitor.StartCalls);
    }

    [Fact]
    public async Task RestartDuringAPass_StaysSuspendedUntilThePassEnds()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        await h.Coordinator.UseEmergencyPassAsync(Ct);
        await h.RestartAsync();

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.Equal(SessionPhase.EmergencyPass, h.Coordinator.Snapshot.Phase);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(h.Firewall.IsEnabled);
        Assert.Single(h.Proxies);
        await h.SpawnAsync(P(20, Game));
        Assert.Empty(h.Controller.Terminated);

        h.AdvanceSession(TimeSpan.FromMinutes(5));
        await h.TickAsync();

        Assert.Equal(SessionPhase.Active, h.Coordinator.Snapshot.Phase);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal(2, h.Proxies.Count);
        Assert.Equal([(20, 20_000L)], h.Controller.Terminated);
    }

    [Fact]
    public async Task Recover_WhileAlreadyRunning_IsANoOp()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.JournalHarness.System.RestoreLog.Clear();

        await h.Coordinator.RecoverOnStartupAsync(Ct);

        Assert.Empty(h.JournalHarness.System.RestoreLog);
        Assert.Single(h.Planner.Inputs);
        Assert.Equal(7, h.JournalHarness.System.Values.Count);
    }

    private static IEnumerable<string> Planned(int port) =>
        new[]
        {
            "dir:" + InstallDir,
            "ifeo:cmd.exe",
            "ifeo:game.exe",
            "ifeo:taskmgr.exe",
            $"pac:{port}",
            "service:Blocker.Service",
            "service:Blocker.Watchdog",
        }.Order(StringComparer.Ordinal);
}
