using Blocker.Core.Orchestration;
using static Blocker.Core.Tests.Orchestration.OrchestrationHarness;

namespace Blocker.Core.Tests.Orchestration;

public sealed class CoordinatorCleanupRetryTests
{
    private const string Stuck = "ifeo:game.exe";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    private static Func<string, Exception?> Locked(List<DateTimeOffset>? attempts = null, OrchestrationHarness? h = null) => target =>
    {
        if (target != Stuck)
        {
            return null;
        }

        attempts?.Add(h!.Time.GetUtcNow());
        return new IOException("locked");
    };

    private static async Task EndSessionWithStuckChangeAsync(OrchestrationHarness h)
    {
        StartEnforcedSessionResult started = await h.StartAsync();
        h.JournalHarness.System.RestoreFault = Locked();
        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));
    }

    private static Task AttemptsAsync(OrchestrationHarness h, long count) =>
        h.AdvanceUntilAsync(h.Coordinator.WaitForCleanupAttemptsAsync(count, Ct), Step);

    [Fact]
    public async Task AFailedRollbackAtSessionEnd_IsRetriedInTheBackground_WithoutAServiceRestart()
    {
        await using var h = new OrchestrationHarness();
        await EndSessionWithStuckChangeAsync(h);
        Assert.True(h.Coordinator.CleanupPending);
        Assert.True(await h.Journal.HasPendingChangesAsync(Ct));

        h.JournalHarness.System.RestoreFault = null; // the antivirus released the key
        await AttemptsAsync(h, 1);

        Assert.False(h.Coordinator.CleanupPending);
        Assert.Empty(h.JournalHarness.System.Values);
        Assert.False(await h.Journal.HasPendingChangesAsync(Ct));
    }

    [Fact]
    public async Task ACompleteRollback_StartsNoRetry()
    {
        await using var h = new OrchestrationHarness();
        StartEnforcedSessionResult started = await h.StartAsync();

        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));

        Assert.False(h.Coordinator.CleanupPending);
        Assert.Equal(0, h.Coordinator.CleanupAttempts);
    }

    [Fact]
    public async Task TheRetryBacksOffBetweenAttempts_AndKeepsTheFlagWhileItFails()
    {
        await using var h = new OrchestrationHarness();
        List<DateTimeOffset> attempts = [];
        StartEnforcedSessionResult started = await h.StartAsync();
        h.JournalHarness.System.RestoreFault = Locked(attempts, h);
        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));

        await AttemptsAsync(h, 3);

        Assert.Equal(4, attempts.Count); // the teardown plus three retries
        Assert.True(attempts[1] - attempts[0] >= EnforcementCoordinator.CleanupRetryInitial);
        Assert.True(attempts[2] - attempts[1] >= 2 * EnforcementCoordinator.CleanupRetryInitial);
        Assert.True(attempts[3] - attempts[2] >= 4 * EnforcementCoordinator.CleanupRetryInitial);
        Assert.True(h.Coordinator.CleanupPending);
    }

    [Theory]
    [InlineData(10, 20)]
    [InlineData(20, 40)]
    [InlineData(160, 300)]
    [InlineData(300, 300)]
    public void TheDelayDoublesUpToTheCap(int currentSeconds, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), EnforcementCoordinator.NextCleanupDelay(TimeSpan.FromSeconds(currentSeconds)));

    [Fact]
    public async Task ANewSession_TakesOverFromTheRetry()
    {
        await using var h = new OrchestrationHarness();
        await EndSessionWithStuckChangeAsync(h);
        h.JournalHarness.System.RestoreFault = null;

        StartEnforcedSessionResult second = await h.StartAsync();
        Assert.True(second.Started);
        Assert.False(h.Coordinator.CleanupPending);
        await h.Coordinator.CleanupRetryTask.WaitAsync(TimeSpan.FromSeconds(30), Ct); // it ends without having tried
        h.Time.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(0, h.Coordinator.CleanupAttempts);
        Assert.Contains(Stuck, h.JournalHarness.System.Values.Keys); // the running session's protections are untouched
    }

    [Fact]
    public async Task StoppingTheCoordinator_StopsTheRetry()
    {
        await using var h = new OrchestrationHarness();
        List<DateTimeOffset> attempts = [];
        StartEnforcedSessionResult started = await h.StartAsync();
        h.JournalHarness.System.RestoreFault = Locked(attempts, h);
        Assert.True(await h.Coordinator.BreakGlassAsync(started.BreakGlassCode!, Ct));
        int before = attempts.Count;

        await h.Coordinator.DisposeAsync(); // waits for the retry to end
        h.Time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(before, attempts.Count);
    }

    [Fact]
    public async Task ASessionThatExpiresOnTheTickLoop_AlsoGetsTheRetry()
    {
        await using var h = new OrchestrationHarness();
        await h.StartAsync();
        h.JournalHarness.System.RestoreFault = Locked();
        h.AdvanceSession(TimeSpan.FromHours(1));
        await h.TickAsync();
        Assert.True(h.Coordinator.CleanupPending);

        h.JournalHarness.System.RestoreFault = null;
        await AttemptsAsync(h, 1);

        Assert.False(h.Coordinator.CleanupPending);
        Assert.Empty(h.JournalHarness.System.Values);
    }

    [Fact]
    public async Task LeftoversThatCannotBeRolledBackAtServiceStart_AreRetriedToo()
    {
        await using var h = new OrchestrationHarness();
        await EndSessionWithStuckChangeAsync(h);
        await h.RestartAsync();

        await h.Coordinator.RecoverOnStartupAsync(Ct);
        Assert.True(h.Coordinator.CleanupPending);

        h.JournalHarness.System.RestoreFault = null;
        await AttemptsAsync(h, 1);

        Assert.False(h.Coordinator.CleanupPending);
        Assert.Empty(h.JournalHarness.System.Values);
    }
}
