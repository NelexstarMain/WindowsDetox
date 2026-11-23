using Blocker.Core.Journal;
using Blocker.Core.Orchestration;
using Blocker.Core.Ports;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorBestEffortTests
{
    [Fact]
    public async Task Start_IfeoRefused_StartsAppliesTheRestAndNamesTheTarget()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t == "ifeo:game.exe" ? new UnauthorizedAccessException("key is protected") : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.True(result.Started);
        Assert.Empty(result.Errors);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("game.exe", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", warning, StringComparison.Ordinal);
        Assert.True(h.Coordinator.IsSessionActive);
        Assert.True(h.Firewall.IsEnabled);
        Assert.Equal(6, h.JournalHarness.System.Values.Count);
        Assert.DoesNotContain("ifeo:game.exe", h.JournalHarness.System.Values.Keys);
        Assert.Contains("ifeo:cmd.exe", h.JournalHarness.System.Values.Keys);
        Assert.Contains($"dir:{InstallDir}", h.JournalHarness.System.Values.Keys);
        Assert.Equal(EnforcementNotificationKind.ProtectionDegraded, Assert.Single(h.Notifications).Kind);
        Assert.Contains("game.exe", h.Notifications[0].Text, StringComparison.Ordinal);

        // The refused change left nothing half applied: its entry is already rolled back, the rest is still applied.
        var entries = await h.EntriesAsync();
        Assert.Equal(JournalEntryState.RolledBack, entries.Single(static e => e.Target == "ifeo:game.exe").State);
        Assert.All(entries.Where(static e => e.Target != "ifeo:game.exe"), static e => Assert.Equal(JournalEntryState.Applied, e.State));
    }

    [Fact]
    public async Task Start_IfeoRefused_FullRollbackLeavesNothingBehind()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t == "ifeo:game.exe" ? new UnauthorizedAccessException() : null;
        Assert.True((await h.StartAsync()).Started);

        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();

        Assert.False(h.Coordinator.IsSessionActive);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.Empty(h.JournalHarness.System.Owners);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task Start_ProtectedDirectoryRefused_StartsAndNamesTheDirectory()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t.StartsWith("dir:", StringComparison.Ordinal) ? new UnauthorizedAccessException() : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.True(result.Started);
        Assert.Contains(InstallDir, Assert.Single(result.Warnings), StringComparison.Ordinal);
        Assert.Equal(6, h.JournalHarness.System.Values.Count);
        Assert.Contains("service:Blocker.Service", h.JournalHarness.System.Values.Keys);

        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();

        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(System.ComponentModel.Win32Exception))]
    [InlineData(typeof(System.Security.SecurityException))]
    public async Task Start_ServiceDaclRefusedWithAnyAccessFault_StillStarts(Type faultType)
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = t => t == "service:Blocker.Watchdog" ? (Exception)Activator.CreateInstance(faultType)! : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.True(result.Started);
        Assert.Contains("Blocker.Watchdog", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_ManyRefusals_ListsFiveTargetsAndCountsTheRest()
    {
        EnforcementSettings settings = Settings with
        {
            ScannedApps = [.. Enumerable.Range(1, 8).Select(static i => new InstalledApp($"App{i}", $@"C:\Apps\app{i}.exe", null, null, null, AppSource.StartMenu))],
        };
        await using var h = new OrchestrationHarness(settings: settings);
        h.JournalHarness.System.ApplyFault = static t => t.StartsWith("ifeo:", StringComparison.Ordinal) ? new UnauthorizedAccessException() : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.True(result.Started);
        string warning = Assert.Single(result.Warnings);
        Assert.Equal(5, warning.Split(".exe", StringSplitOptions.None).Length - 1);
        Assert.Contains("+5 more", warning, StringComparison.Ordinal); // 8 apps + taskmgr + cmd = 10 refused redirects
        Assert.Equal(4, h.JournalHarness.System.Values.Count);
    }

    [Fact]
    public async Task Start_NothingRefused_HasNoWarnings()
    {
        await using var h = new OrchestrationHarness();

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.Empty(result.Warnings);
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public async Task Start_RequiredChangeRefused_StillRollsBackTheWholeSessionWithTheDetailedMessage()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t.StartsWith("pac:", StringComparison.Ordinal) ? new UnauthorizedAccessException() : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.False(result.Started);
        Assert.Empty(result.Warnings);
        string error = Assert.Single(result.Errors);
        Assert.Contains("Applying 'fake' to 'pac:", error, StringComparison.Ordinal);
        Assert.Contains("UnauthorizedAccessException (0x80070005)", error, StringComparison.Ordinal);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(h.Coordinator.IsSessionActive);
    }

    [Fact]
    public async Task Start_BestEffortChangeFailsWithAnUnexpectedException_StillRollsBack()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t == "ifeo:game.exe" ? new InvalidOperationException("bug") : null;

        StartEnforcedSessionResult result = await h.StartAsync();

        Assert.False(result.Started);
        Assert.Contains("InvalidOperationException", Assert.Single(result.Errors), StringComparison.Ordinal);
        Assert.Empty(h.JournalHarness.System.Values);
    }

    [Fact]
    public async Task Start_RefusedChangeCannotBeUndone_StaysPendingSoALaterRollbackCleansIt()
    {
        await using var h = new OrchestrationHarness();
        h.JournalHarness.System.ApplyFault = static t => t == "ifeo:game.exe" ? new UnauthorizedAccessException() : null;
        h.JournalHarness.System.RestoreFault = static t => t == "ifeo:game.exe" ? new UnauthorizedAccessException() : null;

        Assert.True((await h.StartAsync()).Started);

        Assert.Equal(JournalEntryState.Pending, (await h.EntriesAsync()).Single(static e => e.Target == "ifeo:game.exe").State);

        h.JournalHarness.System.RestoreFault = null;
        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();

        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
    }
}
