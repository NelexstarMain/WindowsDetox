using Blocker.Platform.Windows.Processes;

namespace Blocker.Platform.Windows.Tests.Processes;

public sealed class ProcessStartTimeResolverTests
{
    private const long Hour = 36_000_000_000L;
    private const long Created = 133_000_000_000_000_000L;

    private static ProcessStartTimeResolver Resolver(Func<int, long?> query) => new(query);

    [Fact]
    public void ForAStart_TheSystemStartTimeWins_OverAPayloadThatIsAnHourOffInTheRepeatedDstHour()
    {
        var resolver = Resolver(static _ => Created);

        Assert.Equal(Created, resolver.ResolveStart(42, Created + Hour));
    }

    [Fact]
    public void ForAStart_ThePayloadIsUsed_WhenTheProcessCannotBeOpened()
    {
        var resolver = Resolver(static _ => null);

        Assert.Equal(Created + Hour, resolver.ResolveStart(42, Created + Hour));
    }

    [Fact]
    public void ForAStart_ADifferentProcessOnTheReusedPid_IsNotBelieved()
    {
        var resolver = Resolver(static _ => Created + (24 * Hour));

        Assert.Equal(Created, resolver.ResolveStart(42, Created));
    }

    [Fact]
    public void ForAnExit_TheStartTimeResolvedAtStartIsReturned_AndForgotten()
    {
        var resolver = Resolver(static _ => Created);
        _ = resolver.ResolveStart(42, Created + Hour);

        Assert.Equal(Created, resolver.ResolveExit(42, Created + Hour));
        Assert.Equal(Created + Hour, resolver.ResolveExit(42, Created + Hour)); // forgotten: back to the payload
    }

    [Fact]
    public void ForAnExit_OfAnUnseenProcess_ThePayloadIsUsed()
    {
        var resolver = Resolver(static _ => Created);

        Assert.Equal(Created + Hour, resolver.ResolveExit(7, Created + Hour));
    }

    [Fact]
    public void TheRememberedStartTimes_AreBounded()
    {
        var resolver = Resolver(static _ => Created);
        for (int pid = 1; pid <= ProcessStartTimeResolver.MaxRemembered + 10; pid++)
        {
            _ = resolver.ResolveStart(pid, Created);
        }

        Assert.True(resolver.Remembered <= ProcessStartTimeResolver.MaxRemembered);
    }
}
