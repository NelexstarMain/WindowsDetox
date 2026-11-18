using Blocker.Core.Journal;
using Blocker.Core.Security;
using Blocker.Core.ServiceHost;
using Blocker.Core.Session;
using Blocker.Core.Tests.Journal;
using Blocker.Sdk.Session;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class ServiceMaintenanceTests
{
    private readonly JournalHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PersistedSession Session()
    {
        var config = new SessionConfig(TimeSpan.FromHours(1), 1, TimeSpan.FromMinutes(5), false, "UTC");
        return new PersistedSession(
            config,
            new SessionClockState(Guid.NewGuid(), config.Duration, 0, 0, 1, null),
            new EmergencyPassLedgerState(config, "UTC", null, 1, false, 0, 0),
            new BreakGlassHash([1], [2], 1),
            SessionPhase.Active);
    }

    private async Task<RollbackJournal> JournalWithChangesAsync()
    {
        RollbackJournal journal = _h.NewJournal();
        await journal.ApplyAsync("fake", "one", "{}", Ct);
        await journal.ApplyAsync("fake", "two", "{}", Ct);
        return journal;
    }

    [Fact]
    public async Task A_persisted_session_is_never_touched()
    {
        RollbackJournal journal = await JournalWithChangesAsync();
        var sessions = new InMemorySessionStore();
        await sessions.SaveAsync(Session(), Ct);

        MaintenanceResult result = await ServiceMaintenance.RollbackWhenIdleAsync(sessions, journal, Ct);

        Assert.Equal(MaintenanceOutcome.SessionActive, result.Outcome);
        Assert.Equal(2, _h.System.Values.Count);
        Assert.NotNull(await sessions.LoadAsync(Ct));
    }

    [Fact]
    public async Task Without_a_session_the_journal_is_replayed_and_stray_marked_artifacts_are_swept()
    {
        RollbackJournal journal = await JournalWithChangesAsync();
        _h.System.Values["orphan"] = "{}";
        _h.System.Owners["orphan"] = JournalHarness.InstallId;

        MaintenanceResult result = await ServiceMaintenance.RollbackWhenIdleAsync(new InMemorySessionStore(), journal, Ct);

        Assert.Equal(MaintenanceOutcome.Done, result.Outcome);
        Assert.Empty(_h.System.Values);
        Assert.Equal(["two", "one"], _h.System.RestoreLog);
        Assert.Empty(await journal.GetEntriesAsync(Ct));
    }

    [Fact]
    public async Task A_session_file_that_fails_verification_is_discarded_before_the_rollback()
    {
        RollbackJournal journal = await JournalWithChangesAsync();
        var sessions = new SecureSessionStore(_h.Store, _h.Fs.Path.Combine(_h.Fs.Path.GetDirectoryName(_h.JournalPath)!, "session.bin"));
        _h.Fs.File.WriteAllBytes(_h.Fs.Path.Combine(_h.Fs.Path.GetDirectoryName(_h.JournalPath)!, "session.bin"), [1, 2, 3]);

        MaintenanceResult result = await ServiceMaintenance.RollbackWhenIdleAsync(sessions, journal, Ct);

        Assert.Equal(MaintenanceOutcome.Done, result.Outcome);
        Assert.Contains("discarded", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.System.Values);
    }

    [Fact]
    public async Task A_change_that_cannot_be_reverted_is_reported_as_incomplete()
    {
        RollbackJournal journal = await JournalWithChangesAsync();
        _h.System.RestoreFault = target => target == "one" ? new IOException("locked") : null;

        MaintenanceResult result = await ServiceMaintenance.RollbackWhenIdleAsync(new InMemorySessionStore(), journal, Ct);

        Assert.Equal(MaintenanceOutcome.Incomplete, result.Outcome);
        Assert.Contains("could not be reverted", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], ServiceCommandKind.Run, false, false)]
    [InlineData(new[] { "--console" }, ServiceCommandKind.Run, true, false)]
    [InlineData(new[] { "--console", "--dev-mode" }, ServiceCommandKind.Run, true, true)]
    [InlineData(new[] { "--install" }, ServiceCommandKind.Install, false, false)]
    [InlineData(new[] { "--uninstall" }, ServiceCommandKind.Uninstall, false, false)]
    [InlineData(new[] { "--recover" }, ServiceCommandKind.Recover, false, false)]
    [InlineData(new[] { "--dev-abort" }, ServiceCommandKind.DevAbort, false, false)]
    [InlineData(new[] { "--install", "--dev-mode" }, ServiceCommandKind.Install, false, true)]
    public void The_command_line_selects_the_command_and_flags(string[] args, ServiceCommandKind command, bool console, bool devMode)
    {
        Assert.True(ServiceArguments.TryParse(args, out ServiceArguments? parsed, out string? error), error);

        Assert.Equal(new ServiceArguments(command, console, devMode), parsed);
    }

    [Theory]
    [InlineData("--install", "--uninstall")]
    [InlineData("--recover", "--install")]
    [InlineData("--dev-abort", "--recover")]
    [InlineData("--bogus", "--console")]
    public void Conflicting_or_unknown_arguments_are_refused(string first, string second)
    {
        Assert.False(ServiceArguments.TryParse([first, second], out ServiceArguments? parsed, out string? error));

        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
