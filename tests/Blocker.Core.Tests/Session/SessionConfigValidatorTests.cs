using Blocker.Core.Session;

namespace Blocker.Core.Tests.Session;

public sealed class SessionConfigValidatorTests
{
    internal static SessionConfig Valid { get; } = new(TimeSpan.FromHours(2), 2, TimeSpan.FromMinutes(5), false, "UTC");

    [Fact]
    public void ValidConfig_HasNoErrors() => Assert.Empty(SessionConfigValidator.Validate(Valid));

    [Theory]
    [InlineData(15 * 60, true)]
    [InlineData(15 * 60 - 1, false)]
    [InlineData(30 * 24 * 3600, true)]
    [InlineData(30 * 24 * 3600 + 1, false)]
    [InlineData(0, false)]
    [InlineData(-3600, false)]
    public void Duration_Boundaries(int seconds, bool valid)
    {
        var errors = SessionConfigValidator.Validate(Valid with { Duration = TimeSpan.FromSeconds(seconds) });

        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal(["Duration must be between 15 minutes and 30 days."], errors);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(-1, false)]
    [InlineData(11, false)]
    public void PassesPerDay_Boundaries(int passes, bool valid)
    {
        var errors = SessionConfigValidator.Validate(Valid with { EmergencyPassesPerDay = passes });

        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal(["Emergency passes per day must be between 0 and 10."], errors);
        }
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(1800, true)]
    [InlineData(59, false)]
    [InlineData(1801, false)]
    [InlineData(0, false)]
    public void PassDuration_Boundaries(int seconds, bool valid)
    {
        var errors = SessionConfigValidator.Validate(Valid with { EmergencyPassDuration = TimeSpan.FromSeconds(seconds) });

        Assert.Equal(valid, errors.Count == 0);
        if (!valid)
        {
            Assert.Equal(["Emergency pass duration must be between 1 and 30 minutes."], errors);
        }
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Warsaw")]
    [InlineData("America/New_York")]
    public void KnownTimeZone_IsAccepted(string id) => Assert.Empty(SessionConfigValidator.Validate(Valid with { TimeZoneId = id }));

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownTimeZone_IsRejected(string id)
    {
        var errors = SessionConfigValidator.Validate(Valid with { TimeZoneId = id });

        Assert.Equal([$"Time zone '{id}' is not available on this system."], errors);
    }

    [Fact]
    public void MultipleProblems_AreAllReported()
    {
        var errors = SessionConfigValidator.Validate(new(TimeSpan.Zero, 99, TimeSpan.Zero, true, "Nope"));

        Assert.Equal(4, errors.Count);
    }
}
