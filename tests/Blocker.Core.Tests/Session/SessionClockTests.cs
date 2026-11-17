using Blocker.Core.Session;

namespace Blocker.Core.Tests.Session;

public sealed class SessionClockTests
{
    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly TimeSpan Planned = TimeSpan.FromHours(1);
    private static readonly DateTimeOffset StartUtc = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const long Boot = 777;

    private static SessionClockState Started(long tick = 1_000, DateTimeOffset? utc = null) => SessionClock.Start(Id, Planned, tick, Boot, utc);

    private static SessionClockState Checkpoint(SessionClockState s, long tick, long boot = Boot, DateTimeOffset? utc = null) =>
        new SessionClock(s).Checkpoint(tick, boot, utc);

    [Fact]
    public void Start_HasZeroElapsedAndFullRemaining()
    {
        var state = Started(utc: StartUtc);
        var clock = new SessionClock(state);

        Assert.Equal(new SessionClockState(Id, Planned, 0, 1_000, Boot, StartUtc), state);
        Assert.Equal(Planned, clock.Remaining);
        Assert.False(clock.IsExpired);
    }

    [Fact]
    public void SameBoot_ElapsedGrowsByTickDelta()
    {
        var state = Checkpoint(Started(), 61_000);

        Assert.Equal(60_000, state.ElapsedMs);
        Assert.Equal(61_000, state.LastCheckpointTick);
        Assert.Equal(Planned - TimeSpan.FromSeconds(60), new SessionClock(state).Remaining);

        state = Checkpoint(state, 61_000);
        Assert.Equal(60_000, state.ElapsedMs);

        state = Checkpoint(state, 91_000);
        Assert.Equal(90_000, state.ElapsedMs);
    }

    [Fact]
    public void WallClockMovedBack_IsIrrelevant()
    {
        // The state machine never feeds the Windows clock in; the only time input besides ticks is verified UTC, which may even
        // go backwards (e.g. a different source set). Elapsed must still follow ticks only while the boot is unchanged.
        var state = Started(utc: StartUtc);

        state = Checkpoint(state, 11_000, utc: StartUtc.AddHours(-5));
        Assert.Equal(10_000, state.ElapsedMs);

        state = Checkpoint(state, 21_000, utc: null);
        Assert.Equal(20_000, state.ElapsedMs);

        state = Checkpoint(state, 31_000, utc: StartUtc.AddDays(400));
        Assert.Equal(30_000, state.ElapsedMs);
    }

    [Fact]
    public void Reboot_WithoutVerifiedTime_PoweredOffTimeDoesNotCount()
    {
        var state = Checkpoint(Started(utc: StartUtc), 301_000); // 300 s elapsed

        state = Checkpoint(state, 4_000, boot: Boot + 1); // new boot, ticks restarted

        Assert.Equal(300_000, state.ElapsedMs);
        Assert.Equal(4_000, state.LastCheckpointTick);
        Assert.Equal(Boot + 1, state.BootMarker);

        state = Checkpoint(state, 10_000, boot: Boot + 1);
        Assert.Equal(306_000, state.ElapsedMs);
    }

    [Fact]
    public void Reboot_TickGoingBackwardsWithSameMarker_IsTreatedAsReboot()
    {
        var state = Checkpoint(Started(), 501_000); // 500 s

        state = Checkpoint(state, 2_000);

        Assert.Equal(500_000, state.ElapsedMs);
        Assert.Equal(2_000, state.LastCheckpointTick);
    }

    [Fact]
    public void Reboot_ChangedMarkerWithLargerTick_IsStillAReboot()
    {
        var state = Started(tick: 1_000);

        state = Checkpoint(state, 900_000, boot: Boot + 1);

        Assert.Equal(0, state.ElapsedMs); // no delta is trusted across boots
    }

    [Fact]
    public void Reboot_WithVerifiedTime_ElapsedJumpsToVerifiedDistanceFromStart()
    {
        var state = Checkpoint(Started(utc: StartUtc), 301_000); // 300 s elapsed

        state = Checkpoint(state, 4_000, boot: Boot + 1, utc: StartUtc.AddMinutes(30));

        Assert.Equal(1_800_000, state.ElapsedMs);
    }

    [Fact]
    public void Reboot_WithVerifiedTime_NeverDecreasesElapsed()
    {
        var state = Checkpoint(Started(utc: StartUtc), 1_801_000); // 1800 s elapsed

        state = Checkpoint(state, 3_000, boot: Boot + 1, utc: StartUtc.AddMinutes(10)); // verified says only 10 min

        Assert.Equal(1_800_000, state.ElapsedMs);

        state = Checkpoint(state, 4_000, boot: Boot + 2, utc: StartUtc.AddHours(-3)); // verified before start
        Assert.Equal(1_800_000, state.ElapsedMs);
    }

    [Fact]
    public void Reboot_WithVerifiedTime_ButStartNeverVerified_KeepsElapsed_AndBackfillsStart()
    {
        var state = Checkpoint(Started(utc: null), 121_000); // 120 s, start unknown
        Assert.Null(state.StartUtcVerified);

        state = Checkpoint(state, 5_000, boot: Boot + 1, utc: StartUtc);

        Assert.Equal(120_000, state.ElapsedMs);
        Assert.Equal(StartUtc.AddSeconds(-120), state.StartUtcVerified);

        // The backfilled start now protects against a later offline-then-online reboot.
        state = Checkpoint(state, 6_000, boot: Boot + 1);
        state = Checkpoint(state, 2_000, boot: Boot + 2, utc: StartUtc.AddMinutes(20));
        Assert.Equal(1_320_000, state.ElapsedMs);
    }

    [Fact]
    public void SameBoot_VerifiedTime_BackfillsStartOnce()
    {
        var state = Checkpoint(Started(utc: null), 61_000);

        state = Checkpoint(state, 71_000, utc: StartUtc);
        Assert.Equal(StartUtc.AddSeconds(-70), state.StartUtcVerified);

        state = Checkpoint(state, 81_000, utc: StartUtc.AddSeconds(500));
        Assert.Equal(StartUtc.AddSeconds(-70), state.StartUtcVerified);
    }

    [Fact]
    public void KnownStart_IsNeverOverwritten()
    {
        var state = Checkpoint(Started(utc: StartUtc), 11_000, utc: StartUtc.AddDays(1));

        Assert.Equal(StartUtc, state.StartUtcVerified);
    }

    [Fact]
    public void Expiry_Boundaries()
    {
        var almost = Checkpoint(Started(), 1_000 + (long)Planned.TotalMilliseconds - 1);
        var exact = Checkpoint(Started(), 1_000 + (long)Planned.TotalMilliseconds);
        var over = Checkpoint(Started(), 1_000 + (long)Planned.TotalMilliseconds + 5_000);

        Assert.False(new SessionClock(almost).IsExpired);
        Assert.Equal(TimeSpan.FromMilliseconds(1), new SessionClock(almost).Remaining);
        Assert.True(new SessionClock(exact).IsExpired);
        Assert.Equal(TimeSpan.Zero, new SessionClock(exact).Remaining);
        Assert.True(new SessionClock(over).IsExpired);
        Assert.Equal(TimeSpan.Zero, new SessionClock(over).Remaining);
    }

    [Fact]
    public void Elapsed_NeverDecreases_UnderArbitraryInputs()
    {
        var random = new Random(12345);
        var state = Started(utc: StartUtc);
        long tick = 1_000;
        var previous = 0L;

        for (var i = 0; i < 2_000; i++)
        {
            tick = random.Next(5) == 0 ? random.Next(0, 50_000) : tick + random.Next(0, 5_000);
            var boot = random.Next(10) == 0 ? Boot + random.Next(1, 4) : state.BootMarker;
            DateTimeOffset? utc = random.Next(3) switch { 0 => null, 1 => StartUtc.AddSeconds(random.Next(-100_000, 100_000)), _ => StartUtc.AddSeconds(i) };

            state = Checkpoint(state, tick, boot, utc);

            Assert.True(state.ElapsedMs >= previous);
            previous = state.ElapsedMs;
        }
    }
}
