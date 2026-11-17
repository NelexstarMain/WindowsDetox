using System.Text.RegularExpressions;
using Blocker.Core.Session;
using Blocker.Core.Tests.Clock;
using Blocker.Sdk.Events;
using Blocker.Sdk.Session;

namespace Blocker.Core.Tests.Session;

public sealed partial class SessionStateMachineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan PassLength = TimeSpan.FromMinutes(5);
    private static readonly SessionConfig Config = new(Hour, 2, PassLength, false, "UTC");

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}$")]
    private static partial Regex CodeFormat();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingStore : ISessionStore
    {
        private readonly InMemorySessionStore _inner = new();

        public int Saves { get; private set; }

        public PersistedSession? Current { get; private set; }

        public ValueTask<PersistedSession?> LoadAsync(CancellationToken cancellationToken = default) => _inner.LoadAsync(cancellationToken);

        public async ValueTask SaveAsync(PersistedSession session, CancellationToken cancellationToken = default)
        {
            Saves++;
            Current = session;
            await _inner.SaveAsync(session, cancellationToken);
        }

        public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
        {
            Current = null;
            await _inner.ClearAsync(cancellationToken);
        }
    }

    private sealed class Harness
    {
        public Harness(DateTimeOffset? utc = null, bool allowAbort = false)
        {
            Verified.Utc = utc ?? Noon;
            Machine = NewMachine(allowAbort);
        }

        public FakeMonotonicClock Mono { get; } = new(100_000);

        public FakeVerifiedClock Verified { get; } = new();

        public RecordingPublisher Events { get; } = new();

        public CountingStore Store { get; } = new();

        public long Boot { get; set; } = 5_000;

        public SessionStateMachine Machine { get; }

        public SessionStateMachine NewMachine(bool allowAbort = false) => new(Store, Events, Mono, Verified, () => Boot, allowAbort, 1_000);

        public void Advance(TimeSpan by)
        {
            Mono.Advance(by);
            Verified.Utc += by;
        }

        public void Reboot(DateTimeOffset? verifiedUtc)
        {
            Boot++;
            Mono.TickCount64 = 3_000;
            Verified.Utc = verifiedUtc;
        }

        public async Task<(Guid Id, string Code)> StartAsync(SessionConfig? config = null)
        {
            var result = await Machine.StartAsync(config ?? Config, Ct);
            Assert.True(result.Started);
            return (result.SessionId!.Value, result.BreakGlassCode!);
        }
    }

    [Fact]
    public async Task Start_FromIdle_RunsSessionAndShowsCodeOnce()
    {
        var h = new Harness();

        var result = await h.Machine.StartAsync(Config, Ct);

        Assert.True(result.Started);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.SessionId);
        Assert.Matches(CodeFormat(), result.BreakGlassCode!);
        var id = result.SessionId.Value;
        Assert.Equal(new SessionSnapshot(SessionPhase.Active, id, Hour, null, 2, 2, PassLength), h.Machine.Snapshot);
        Assert.Equal([new SessionStarted(id, Hour, Noon)], h.Events.Events);

        var persisted = h.Store.Current!;
        Assert.Equal(SessionPhase.Active, persisted.Phase);
        Assert.Equal(Config, persisted.Config);
        Assert.Equal(id, persisted.Clock.SessionId);
        Assert.Equal(Noon, persisted.Clock.StartUtcVerified);
        Assert.Equal(2, persisted.Ledger.PassesLeft);
        Assert.True(BreakGlassCode.Verify(result.BreakGlassCode, persisted.BreakGlass));
    }

    [Fact]
    public async Task Start_WhileRunning_IsRefused_WithoutSideEffects()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();

        var second = await h.Machine.StartAsync(Config with { Duration = TimeSpan.FromDays(1) }, Ct);

        Assert.False(second.Started);
        Assert.Null(second.SessionId);
        Assert.Null(second.BreakGlassCode);
        Assert.Equal(["A session is already running."], second.Errors);
        Assert.Equal(id, h.Machine.Snapshot.SessionId);
        Assert.Equal(Hour, h.Store.Current!.Config.Duration);
        Assert.Single(h.Events.Events);
    }

    [Fact]
    public async Task Start_WithInvalidConfig_ReturnsErrors_AndStaysIdle()
    {
        var h = new Harness();

        var result = await h.Machine.StartAsync(Config with { Duration = TimeSpan.FromMinutes(1) }, Ct);

        Assert.False(result.Started);
        Assert.Equal(["Duration must be between 15 minutes and 30 days."], result.Errors);
        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Empty(h.Events.Events);
    }

    [Fact]
    public async Task Start_WhenPersistedSessionExistsButWasNotRestored_IsRefused()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        var freshAfterRestart = h.NewMachine();

        var result = await freshAfterRestart.StartAsync(Config with { Duration = TimeSpan.FromMinutes(20) }, Ct);

        Assert.False(result.Started);
        Assert.Equal(id, h.Store.Current!.Clock.SessionId);
        Assert.Equal(Hour, h.Store.Current.Config.Duration);
    }

    [Fact]
    public async Task Tick_AdvancesRemainingByMonotonicTime_AndPersistsEveryCall()
    {
        var h = new Harness();
        await h.StartAsync();
        Assert.Equal(1, h.Store.Saves);

        h.Advance(TimeSpan.FromSeconds(60));
        var outcome = await h.Machine.TickAsync(Ct);

        Assert.Equal(TickOutcome.None, outcome);
        Assert.Equal(Hour - TimeSpan.FromSeconds(60), h.Machine.Snapshot.Remaining);
        Assert.Equal(60_000, h.Store.Current!.Clock.ElapsedMs);
        Assert.Equal(2, h.Store.Saves);

        await h.Machine.TickAsync(Ct);
        Assert.Equal(3, h.Store.Saves);
        Assert.Equal(60_000, h.Store.Current.Clock.ElapsedMs);
    }

    [Fact]
    public async Task Tick_WhenIdle_DoesNothing()
    {
        var h = new Harness();

        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct));
        Assert.Equal(0, h.Store.Saves);
        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
    }

    [Fact]
    public async Task VerifiedTimeJumping_NeverChangesRemaining_WhileBootIsUnchanged()
    {
        var h = new Harness();
        await h.StartAsync();

        h.Mono.Advance(TimeSpan.FromSeconds(60));
        h.Verified.Utc = Noon.AddDays(-5);
        await h.Machine.TickAsync(Ct);
        Assert.Equal(Hour - TimeSpan.FromSeconds(60), h.Machine.Snapshot.Remaining);

        h.Mono.Advance(TimeSpan.FromSeconds(60));
        h.Verified.Utc = Noon.AddDays(5);
        await h.Machine.TickAsync(Ct);
        Assert.Equal(Hour - TimeSpan.FromSeconds(120), h.Machine.Snapshot.Remaining);
    }

    [Fact]
    public async Task EmergencyPass_FullFlow()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        Assert.Equal(new SessionSnapshot(SessionPhase.EmergencyPass, id, Hour - TimeSpan.FromMinutes(10), PassLength, 1, 2, PassLength), h.Machine.Snapshot);
        Assert.Equal(SessionPhase.EmergencyPass, h.Store.Current!.Phase);
        Assert.Equal(new EmergencyPassStarted(id, PassLength, 1, Noon.AddMinutes(10)), h.Events.Events[^1]);

        Assert.Equal(EmergencyPassResult.AlreadyActive, await h.Machine.RequestEmergencyPassAsync(Ct));
        Assert.Equal(1, h.Machine.Snapshot.EmergencyPassesLeftToday);
        Assert.Equal(2, h.Events.Events.Count);

        h.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct));
        Assert.Equal(TimeSpan.FromMinutes(2), h.Machine.Snapshot.EmergencyPassRemaining);

        h.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new TickOutcome(PassWarningDue: true, PassEnded: false, SessionExpired: false), await h.Machine.TickAsync(Ct));
        Assert.Equal(TimeSpan.FromMinutes(1), h.Machine.Snapshot.EmergencyPassRemaining);

        h.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct)); // warning is raised once per pass
        Assert.Equal(TimeSpan.FromSeconds(50), h.Machine.Snapshot.EmergencyPassRemaining);

        h.Advance(TimeSpan.FromSeconds(50));
        Assert.Equal(new TickOutcome(false, PassEnded: true, false), await h.Machine.TickAsync(Ct));
        Assert.Equal(SessionPhase.Active, h.Machine.Snapshot.Phase);
        Assert.Null(h.Machine.Snapshot.EmergencyPassRemaining);
        Assert.Equal(1, h.Machine.Snapshot.EmergencyPassesLeftToday);
        Assert.Equal(new EmergencyPassEnded(id, Noon.AddMinutes(15)), h.Events.Events[^1]);
        Assert.Equal(3, h.Events.Events.Count);

        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct));
        Assert.Equal(3, h.Events.Events.Count);
    }

    [Fact]
    public async Task EmergencyPass_Exhaustion_AndNotInSession()
    {
        var h = new Harness();
        Assert.Equal(EmergencyPassResult.NotInSession, await h.Machine.RequestEmergencyPassAsync(Ct));
        await h.StartAsync();

        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(PassLength);
        Assert.True((await h.Machine.TickAsync(Ct)).PassEnded);
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(PassLength);
        Assert.True((await h.Machine.TickAsync(Ct)).PassEnded);

        Assert.Equal(EmergencyPassResult.NoPassesLeft, await h.Machine.RequestEmergencyPassAsync(Ct));
        Assert.Equal(0, h.Machine.Snapshot.EmergencyPassesLeftToday);
        Assert.Equal(SessionPhase.Active, h.Machine.Snapshot.Phase);
    }

    [Fact]
    public async Task EmergencyPass_RequestEndsAnExpiredPassBeforeDeciding()
    {
        var h = new Harness();
        await h.StartAsync();
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(TimeSpan.FromMinutes(6)); // no tick in between

        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));

        Assert.Equal(0, h.Machine.Snapshot.EmergencyPassesLeftToday);
        Assert.IsType<EmergencyPassEnded>(h.Events.Events[^2]);
        Assert.IsType<EmergencyPassStarted>(h.Events.Events[^1]);
    }

    [Fact]
    public async Task Expiry_EndsSession_ClearsStore_AndAllowsNewSession()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();

        h.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct));
        Assert.Equal(TimeSpan.FromMinutes(1), h.Machine.Snapshot.Remaining);

        h.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new TickOutcome(false, false, SessionExpired: true), await h.Machine.TickAsync(Ct));

        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Null(await h.Store.LoadAsync(Ct));
        Assert.Equal(new SessionEnded(id, SessionEndReason.Expired, Noon.AddHours(1)), h.Events.Events[^1]);
        Assert.Equal(2, h.Events.Events.Count);
        Assert.Equal(TickOutcome.None, await h.Machine.TickAsync(Ct));

        Assert.True((await h.Machine.StartAsync(Config, Ct)).Started);
    }

    [Fact]
    public async Task Expiry_DuringPass_ReportsPassEndedAndSessionExpired_InOrder()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(57));
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));

        h.Advance(TimeSpan.FromMinutes(3));
        var outcome = await h.Machine.TickAsync(Ct);

        Assert.Equal(new TickOutcome(PassWarningDue: false, PassEnded: true, SessionExpired: true), outcome);
        Assert.Equal(
            [new EmergencyPassEnded(id, Noon.AddHours(1)), new SessionEnded(id, SessionEndReason.Expired, Noon.AddHours(1))],
            h.Events.Events.TakeLast(2).ToArray());
    }

    [Fact]
    public async Task Expiry_IsDetectedByPassRequest_WhichThenReportsNotInSession()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        h.Advance(TimeSpan.FromHours(2));

        Assert.Equal(EmergencyPassResult.NotInSession, await h.Machine.RequestEmergencyPassAsync(Ct));

        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Equal(new SessionEnded(id, SessionEndReason.Expired, Noon.AddHours(2)), h.Events.Events[^1]);
    }

    [Fact]
    public async Task BreakGlass_WrongCodeKeepsSession_RightCodeEndsIt_Once()
    {
        var h = new Harness();
        var (id, code) = await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(5));

        Assert.False(await h.Machine.TryBreakGlassAsync("AAAAA-AAAAA-AAAAA-AAAAA", Ct));
        Assert.False(await h.Machine.TryBreakGlassAsync(string.Empty, Ct));
        Assert.Equal(SessionPhase.Active, h.Machine.Snapshot.Phase);
        Assert.NotNull(h.Store.Current);
        Assert.Single(h.Events.Events);

        Assert.True(await h.Machine.TryBreakGlassAsync(code.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant(), Ct));

        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Equal(new SessionEnded(id, SessionEndReason.BreakGlass, Noon.AddMinutes(5)), h.Events.Events[^1]);
        Assert.False(await h.Machine.TryBreakGlassAsync(code, Ct));
        Assert.Equal(2, h.Events.Events.Count);
    }

    [Fact]
    public async Task BreakGlass_DuringPass_EndsPassThenSession()
    {
        var h = new Harness();
        var (id, code) = await h.StartAsync();
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));

        Assert.True(await h.Machine.TryBreakGlassAsync(code, Ct));

        Assert.Equal(
            [new EmergencyPassEnded(id, Noon), new SessionEnded(id, SessionEndReason.BreakGlass, Noon)],
            h.Events.Events.TakeLast(2).ToArray());
    }

    [Fact]
    public async Task BreakGlass_WhenIdle_IsFalse()
    {
        var h = new Harness();

        Assert.False(await h.Machine.TryBreakGlassAsync("AAAAA-AAAAA-AAAAA-AAAAA", Ct));
    }

    [Fact]
    public async Task DeveloperAbort_Disabled_NeverEndsTheSession()
    {
        var h = new Harness(allowAbort: false);
        await h.StartAsync();

        Assert.False(await h.Machine.TryDeveloperAbortAsync(Ct));

        Assert.Equal(SessionPhase.Active, h.Machine.Snapshot.Phase);
        Assert.NotNull(h.Store.Current);
        Assert.Single(h.Events.Events);
    }

    [Fact]
    public async Task DeveloperAbort_Enabled_EndsSessionWithDeveloperAbortReason()
    {
        var h = new Harness(allowAbort: true);
        var (id, _) = await h.StartAsync();

        Assert.True(await h.Machine.TryDeveloperAbortAsync(Ct));

        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Equal(new SessionEnded(id, SessionEndReason.DeveloperAbort, Noon), h.Events.Events[^1]);
        Assert.False(await h.Machine.TryDeveloperAbortAsync(Ct));
    }

    [Fact]
    public async Task Restore_NothingPersisted_StaysIdle()
    {
        var h = new Harness();

        Assert.Equal(TickOutcome.None, await h.Machine.RestoreAsync(Ct));
        Assert.Equal(SessionSnapshot.Idle, h.Machine.Snapshot);
        Assert.Empty(h.Events.Events);
    }

    [Fact]
    public async Task Restore_SameBoot_ContinuesWithMonotonicTime()
    {
        var h = new Harness();
        var (id, code) = await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(10));
        var restarted = h.NewMachine();

        Assert.Equal(TickOutcome.None, await restarted.RestoreAsync(Ct));

        Assert.Equal(id, restarted.Snapshot.SessionId);
        Assert.Equal(SessionPhase.Active, restarted.Snapshot.Phase);
        Assert.Equal(TimeSpan.FromMinutes(50), restarted.Snapshot.Remaining);
        Assert.Single(h.Events.Events);

        h.Advance(TimeSpan.FromMinutes(5));
        await restarted.TickAsync(Ct);
        Assert.Equal(TimeSpan.FromMinutes(45), restarted.Snapshot.Remaining);
        Assert.True(await restarted.TryBreakGlassAsync(code, Ct));
    }

    [Fact]
    public async Task Restore_AfterOfflineReboot_PoweredOffTimeDoesNotCount()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(10));
        await h.Machine.TickAsync(Ct);
        h.Reboot(verifiedUtc: null);
        var restarted = h.NewMachine();

        Assert.Equal(TickOutcome.None, await restarted.RestoreAsync(Ct));

        Assert.Equal(TimeSpan.FromMinutes(50), restarted.Snapshot.Remaining);
        Assert.Equal(10 * 60_000, h.Store.Current!.Clock.ElapsedMs);
    }

    [Fact]
    public async Task Restore_AfterReboot_WithVerifiedTime_CountsVerifiedDistanceFromStart()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(10));
        await h.Machine.TickAsync(Ct);
        h.Reboot(verifiedUtc: Noon.AddMinutes(40));
        var restarted = h.NewMachine();

        await restarted.RestoreAsync(Ct);

        Assert.Equal(TimeSpan.FromMinutes(20), restarted.Snapshot.Remaining);
    }

    [Fact]
    public async Task Restore_AfterReboot_WithVerifiedTimeBeforeStart_DoesNotShortenElapsed()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Advance(TimeSpan.FromMinutes(10));
        await h.Machine.TickAsync(Ct);
        h.Reboot(verifiedUtc: Noon.AddDays(-30));
        var restarted = h.NewMachine();

        await restarted.RestoreAsync(Ct);

        Assert.Equal(TimeSpan.FromMinutes(50), restarted.Snapshot.Remaining);
    }

    [Fact]
    public async Task Restore_ExpiredSession_IsEnded()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        h.Reboot(verifiedUtc: Noon.AddHours(2));
        var restarted = h.NewMachine();

        var outcome = await restarted.RestoreAsync(Ct);

        Assert.Equal(new TickOutcome(false, false, SessionExpired: true), outcome);
        Assert.Equal(SessionSnapshot.Idle, restarted.Snapshot);
        Assert.Null(h.Store.Current);
        Assert.Null(await h.Store.LoadAsync(Ct));
        Assert.Equal(new SessionEnded(id, SessionEndReason.Expired, Noon.AddHours(2)), h.Events.Events[^1]);
    }

    [Fact]
    public async Task Restore_RunningPass_AfterSameBootRestart_Continues()
    {
        var h = new Harness();
        await h.StartAsync();
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(TimeSpan.FromMinutes(2));
        var restarted = h.NewMachine();

        Assert.Equal(TickOutcome.None, await restarted.RestoreAsync(Ct));

        Assert.Equal(SessionPhase.EmergencyPass, restarted.Snapshot.Phase);
        Assert.Equal(TimeSpan.FromMinutes(3), restarted.Snapshot.EmergencyPassRemaining);
        Assert.Equal(1, restarted.Snapshot.EmergencyPassesLeftToday);
    }

    [Fact]
    public async Task Restore_RunningPass_AfterReboot_IsEnded_AndPassIsNotRefunded()
    {
        var h = new Harness();
        var (id, _) = await h.StartAsync();
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(TimeSpan.FromMinutes(1));
        await h.Machine.TickAsync(Ct);
        h.Reboot(verifiedUtc: null);
        var restarted = h.NewMachine();

        var outcome = await restarted.RestoreAsync(Ct);

        Assert.Equal(new TickOutcome(false, PassEnded: true, false), outcome);
        Assert.Equal(SessionPhase.Active, restarted.Snapshot.Phase);
        Assert.Null(restarted.Snapshot.EmergencyPassRemaining);
        Assert.Equal(1, restarted.Snapshot.EmergencyPassesLeftToday);
        Assert.Equal(new EmergencyPassEnded(id, DateTimeOffset.UnixEpoch), h.Events.Events[^1]);
    }

    [Fact]
    public async Task EventTimestamps_UseVerifiedUtc_OrUnixEpoch_NeverTheWallClock()
    {
        var offline = new Harness();
        offline.Verified.Utc = null;
        var (id, code) = await offline.StartAsync();

        Assert.Equal([new SessionStarted(id, Hour, DateTimeOffset.UnixEpoch)], offline.Events.Events);

        offline.Verified.Utc = Noon.AddMinutes(30);
        Assert.True(await offline.Machine.TryBreakGlassAsync(code, Ct));
        Assert.Equal(new SessionEnded(id, SessionEndReason.BreakGlass, Noon.AddMinutes(30)), offline.Events.Events[^1]);
    }

    [Fact]
    public async Task DayRollover_RefillsOnlyWithVerifiedTime_InTheFrozenZone()
    {
        var h = new Harness(utc: new DateTimeOffset(2026, 11, 17, 23, 50, 0, TimeSpan.Zero));
        await h.StartAsync();
        Assert.Equal(EmergencyPassResult.Started, await h.Machine.RequestEmergencyPassAsync(Ct));
        h.Advance(PassLength);
        await h.Machine.TickAsync(Ct);
        Assert.Equal(1, h.Machine.Snapshot.EmergencyPassesLeftToday);

        h.Verified.Utc = null; // time can no longer be verified
        h.Mono.Advance(TimeSpan.FromMinutes(20));
        await h.Machine.TickAsync(Ct);
        Assert.Equal(1, h.Machine.Snapshot.EmergencyPassesLeftToday);

        h.Verified.Utc = new DateTimeOffset(2026, 11, 18, 0, 30, 0, TimeSpan.Zero);
        await h.Machine.TickAsync(Ct);
        Assert.Equal(2, h.Machine.Snapshot.EmergencyPassesLeftToday);
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerialized()
    {
        var h = new Harness();
        await h.StartAsync(Config with { EmergencyPassesPerDay = 3 });

        var requests = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => await h.Machine.RequestEmergencyPassAsync(Ct), Ct)).ToArray();
        var ticks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () => await h.Machine.TickAsync(Ct), Ct)).ToArray();
        var results = await Task.WhenAll(requests);
        await Task.WhenAll(ticks);

        Assert.Equal(1, results.Count(r => r == EmergencyPassResult.Started));
        Assert.Equal(19, results.Count(r => r == EmergencyPassResult.AlreadyActive));
        Assert.Equal(2, h.Machine.Snapshot.EmergencyPassesLeftToday);
        Assert.Equal(2, h.Events.Events.Count);
    }

    [Fact]
    public async Task ISessionInfo_ExposesSnapshot()
    {
        var h = new Harness();
        await h.StartAsync();

        Assert.Same(h.Machine.Snapshot, ((ISessionInfo)h.Machine).Current);
    }
}
