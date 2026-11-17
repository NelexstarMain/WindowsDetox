using Blocker.Core.Session;

namespace Blocker.Core.Tests.Session;

public sealed class EmergencyPassLedgerTests
{
    private static readonly SessionConfig Config = new(TimeSpan.FromDays(3), 2, TimeSpan.FromMinutes(5), false, "Europe/Warsaw");
    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 11, 0, 0, TimeSpan.Zero); // 12:00 in Warsaw

    private static EmergencyPassLedger Create(SessionConfig? config = null, DateTimeOffset? utc = null) =>
        EmergencyPassLedger.Create(config ?? Config, 1_000, utc ?? Noon);

    [Fact]
    public void Create_GrantsDailyPasses_AndStampsDayInFrozenZone()
    {
        var ledger = Create();

        Assert.Equal(2, ledger.PassesLeft);
        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey);
        Assert.Equal("Europe/Warsaw", ledger.State.FrozenTimeZoneId);
        Assert.False(ledger.IsPassActive);
        Assert.Equal(TimeSpan.Zero, ledger.PassRemaining);
        Assert.False(ledger.WarningDue);
    }

    [Fact]
    public void Create_WithoutVerifiedTime_GrantsFirstDayButNoDayKey()
    {
        var ledger = EmergencyPassLedger.Create(Config, 1_000, null);

        Assert.Equal(2, ledger.PassesLeft);
        Assert.Null(ledger.State.DayKey);
    }

    [Fact]
    public void FirstVerifiedTimeAfterOfflineStart_SetsDayKeyWithoutRefill()
    {
        var ledger = EmergencyPassLedger.Create(Config, 1_000, null);
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(1_000 + 400_000, null);
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(1_500_000, Noon);

        Assert.Equal(1, ledger.PassesLeft);
        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey);
    }

    [Fact]
    public void NewDayInFrozenZone_RefillsToDailyAmount()
    {
        var ledger = Create(utc: new DateTimeOffset(2026, 11, 17, 22, 0, 0, TimeSpan.Zero));
        Assert.True(ledger.TryStartPass(1_000));
        Assert.True(ledger.Update(1_000 + 300_000, new DateTimeOffset(2026, 11, 17, 22, 59, 0, TimeSpan.Zero)));
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(1_400_000, new DateTimeOffset(2026, 11, 17, 22, 59, 59, TimeSpan.Zero)); // 23:59:59 in Warsaw
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(1_401_000, new DateTimeOffset(2026, 11, 17, 23, 0, 0, TimeSpan.Zero)); // 00:00 in Warsaw
        Assert.Equal(2, ledger.PassesLeft);
        Assert.Equal(new DateOnly(2026, 11, 18), ledger.State.DayKey);
    }

    [Fact]
    public void WithoutVerifiedTime_NothingIsEverRefilled()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(2_000_000, null);
        Assert.False(ledger.IsPassActive);

        for (var days = 1; days <= 5; days++)
        {
            ledger.Update(2_000_000 + days * 86_400_000L, null);
        }

        Assert.Equal(1, ledger.PassesLeft);
        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey);
    }

    [Theory]
    [InlineData(2026, 3, 29, 21, 59, 2026, 3, 29)] // before midnight CEST on the spring-forward day
    [InlineData(2026, 3, 29, 22, 0, 2026, 3, 30)] // midnight CEST (23 h day)
    [InlineData(2026, 3, 28, 22, 59, 2026, 3, 28)] // 23:59 CET the day before
    [InlineData(2026, 3, 28, 23, 0, 2026, 3, 29)] // midnight CET
    [InlineData(2026, 10, 25, 22, 59, 2026, 10, 25)] // 23:59 CET on the fall-back day (25 h day)
    [InlineData(2026, 10, 25, 23, 0, 2026, 10, 26)] // midnight CET
    [InlineData(2026, 10, 24, 21, 59, 2026, 10, 24)] // 23:59 CEST
    [InlineData(2026, 10, 24, 22, 0, 2026, 10, 25)] // midnight CEST
    public void DayKey_FollowsLocalMidnightAcrossDst(int y, int mo, int d, int h, int mi, int ey, int emo, int ed)
    {
        var ledger = EmergencyPassLedger.Create(Config, 0, new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(ey, emo, ed), ledger.State.DayKey);
    }

    [Fact]
    public void Rollover_AcrossSpringForwardDay_Refills()
    {
        var ledger = EmergencyPassLedger.Create(Config, 0, new DateTimeOffset(2026, 3, 29, 21, 59, 0, TimeSpan.Zero));
        Assert.True(ledger.TryStartPass(0));
        ledger.Update(300_000, new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero).AddSeconds(-1));
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(301_000, new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, ledger.PassesLeft);
    }

    [Fact]
    public void FrozenZone_WinsOverAnyOtherZoneInTheConfig()
    {
        // Someone swaps the zone id in the config after session start; only the frozen id is used.
        var tampered = Config with { TimeZoneId = "Pacific/Kiritimati" };
        var state = new EmergencyPassLedgerState(tampered, "UTC", null, 2, false, 0, 0);
        var ledger = new EmergencyPassLedger(state);

        ledger.Update(1_000, new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey); // Kiritimati (UTC+14) would say Nov 18
    }

    [Fact]
    public void LocalSystemZoneIsNotConsulted_SameInstantDiffersOnlyByFrozenZone()
    {
        var instant = new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.Zero);

        var warsaw = EmergencyPassLedger.Create(Config, 0, instant);
        var newYork = EmergencyPassLedger.Create(Config with { TimeZoneId = "America/New_York" }, 0, instant);

        Assert.Equal(new DateOnly(2026, 11, 18), warsaw.State.DayKey);
        Assert.Equal(new DateOnly(2026, 11, 17), newYork.State.DayKey);
    }

    [Fact]
    public void UnresolvableFrozenZone_FallsBackToUtc()
    {
        var ledger = new EmergencyPassLedger(new(Config, "Gone/Zone", null, 2, false, 0, 0));

        ledger.Update(1, new DateTimeOffset(2026, 11, 17, 23, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey);
    }

    [Fact]
    public void VerifiedTimeGoingBackToAnEarlierDay_NeverRefills_OrRewindsDayKey()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(400_000, Noon.AddMinutes(10));
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(500_000, Noon.AddDays(-3));

        Assert.Equal(1, ledger.PassesLeft);
        Assert.Equal(new DateOnly(2026, 11, 17), ledger.State.DayKey);
    }

    [Fact]
    public void SameDay_NoRefill()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(400_000, Noon.AddHours(5));

        Assert.Equal(1, ledger.PassesLeft);
    }

    [Fact]
    public void CarryOver_AddsDailyAmount_CappedAtThreeTimesDaily()
    {
        var ledger = Create(Config with { CarryOverUnusedPasses = true });
        Assert.Equal(2, ledger.PassesLeft);

        ledger.Update(1, Noon.AddDays(1));
        Assert.Equal(4, ledger.PassesLeft);

        ledger.Update(2, Noon.AddDays(2));
        Assert.Equal(6, ledger.PassesLeft);

        ledger.Update(3, Noon.AddDays(3));
        Assert.Equal(6, ledger.PassesLeft); // 3 x 2

        ledger.Update(4, Noon.AddDays(30));
        Assert.Equal(6, ledger.PassesLeft);
    }

    [Fact]
    public void CarryOver_UsedPassesReduceTheCarry()
    {
        var ledger = Create(Config with { CarryOverUnusedPasses = true });
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(400_000, Noon.AddMinutes(10));
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(400_001, Noon.AddDays(1));

        Assert.Equal(3, ledger.PassesLeft);
    }

    [Fact]
    public void CarryOver_MultiDayGap_AddsOneDailyAmountPerDayCrossed()
    {
        var ledger = Create(Config with { CarryOverUnusedPasses = true, EmergencyPassesPerDay = 3 });
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(500_000, Noon.AddMinutes(1));
        Assert.Equal(2, ledger.PassesLeft);

        ledger.Update(500_001, Noon.AddDays(2)); // two days crossed: 2 + 2 * 3 = 8, cap 9

        Assert.Equal(8, ledger.PassesLeft);
    }

    [Fact]
    public void WithoutCarryOver_RefillIsToDailyAmount_NotAdded()
    {
        var ledger = Create();

        ledger.Update(1, Noon.AddDays(1));

        Assert.Equal(2, ledger.PassesLeft);
    }

    [Fact]
    public void ZeroPassesPerDay_NeverGrantsAnything()
    {
        var ledger = Create(Config with { EmergencyPassesPerDay = 0, CarryOverUnusedPasses = true });

        ledger.Update(1, Noon.AddDays(10));

        Assert.Equal(0, ledger.PassesLeft);
        Assert.False(ledger.TryStartPass(2));
    }

    [Fact]
    public void TryStartPass_ConsumesOne_AndRejectsWhileActiveOrExhausted()
    {
        var ledger = Create();

        Assert.True(ledger.TryStartPass(1_000));
        Assert.True(ledger.IsPassActive);
        Assert.Equal(1, ledger.PassesLeft);
        Assert.Equal(TimeSpan.FromMinutes(5), ledger.PassRemaining);
        Assert.False(ledger.TryStartPass(1_000));
        Assert.Equal(1, ledger.PassesLeft);

        ledger.Update(1_000 + 300_000, Noon);
        Assert.False(ledger.IsPassActive);

        Assert.True(ledger.TryStartPass(301_000));
        ledger.Update(301_000 + 300_000, Noon);
        Assert.Equal(0, ledger.PassesLeft);
        Assert.False(ledger.TryStartPass(700_000));
        Assert.False(ledger.IsPassActive);
    }

    [Fact]
    public void Pass_RemainingAndWarningThreshold_AreExact()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(10_000));

        Assert.False(ledger.Update(10_000 + 238_000, Noon)); // 62 s left
        Assert.Equal(TimeSpan.FromSeconds(62), ledger.PassRemaining);
        Assert.False(ledger.WarningDue);

        Assert.False(ledger.Update(10_000 + 239_000, Noon)); // 61 s left
        Assert.False(ledger.WarningDue);

        Assert.False(ledger.Update(10_000 + 240_000, Noon)); // exactly 60 s left
        Assert.Equal(TimeSpan.FromSeconds(60), ledger.PassRemaining);
        Assert.True(ledger.WarningDue);

        Assert.False(ledger.Update(10_000 + 299_999, Noon));
        Assert.Equal(TimeSpan.FromMilliseconds(1), ledger.PassRemaining);
        Assert.True(ledger.WarningDue);

        Assert.True(ledger.Update(10_000 + 300_000, Noon));
        Assert.False(ledger.IsPassActive);
        Assert.False(ledger.WarningDue);
        Assert.Equal(TimeSpan.Zero, ledger.PassRemaining);
    }

    [Fact]
    public void PassEnd_IsReportedExactlyOnce()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(0));

        Assert.True(ledger.Update(400_000, Noon));
        Assert.False(ledger.Update(401_000, Noon));
        Assert.False(ledger.Update(402_000, Noon));
    }

    [Fact]
    public void Update_WithoutPass_ReportsNothing_AndTracksTick()
    {
        var ledger = Create();

        Assert.False(ledger.Update(5_000, Noon));
        Assert.Equal(5_000, ledger.State.LastTick);
    }

    [Fact]
    public void PassTimesOnTicks_NotOnVerifiedTime()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(0));

        Assert.False(ledger.Update(60_000, Noon.AddDays(-1))); // verified time moved back: irrelevant
        Assert.Equal(TimeSpan.FromMinutes(4), ledger.PassRemaining);
        Assert.False(ledger.Update(120_000, null));
        Assert.Equal(TimeSpan.FromMinutes(3), ledger.PassRemaining);
    }

    [Fact]
    public void TickCounterGoingBackwards_EndsRunningPass()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(500_000));

        Assert.True(ledger.Update(1_000, Noon));
        Assert.False(ledger.IsPassActive);
        Assert.Equal(1, ledger.PassesLeft);
    }

    [Fact]
    public void ReportedReboot_EndsRunningPass()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(1_000));

        Assert.True(ledger.Update(2_000, Noon, rebooted: true));
        Assert.False(ledger.IsPassActive);
        Assert.Equal(TimeSpan.Zero, ledger.PassRemaining);
    }

    [Fact]
    public void State_RoundTripsThroughRecordCopy()
    {
        var ledger = Create();
        Assert.True(ledger.TryStartPass(1_000));
        ledger.Update(61_000, Noon);

        var restored = new EmergencyPassLedger(ledger.State with { });

        Assert.Equal(ledger.State, restored.State);
        Assert.Equal(TimeSpan.FromMinutes(4), restored.PassRemaining);
        Assert.True(restored.IsPassActive);
    }
}
