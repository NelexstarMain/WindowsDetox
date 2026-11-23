using Blocker.Setup;

namespace Blocker.Core.Tests.ServiceHost;

public sealed class SetupUninstallCheckTests
{
    private const string SessionActive = "session-active";
    private const string Incomplete = "incomplete";

    private static SetupException Fail(int code) =>
        Assert.Throws<SetupException>(() => ServiceUninstallCheck.EnsureSucceeded(code, SessionActive, Incomplete));

    [Fact]
    public void ExitCodeZero_Proceeds() => ServiceUninstallCheck.EnsureSucceeded(0, SessionActive, Incomplete);

    [Fact]
    public void ARunningSession_UsesItsOwnMessage() => Assert.Equal(SessionActive, Fail(3).Message);

    [Fact]
    public void AnIncompleteRollback_UsesItsOwnMessage() => Assert.Equal(Incomplete, Fail(4).Message);

    [Theory]
    [InlineData(1)] // failed: the service did not stop, DPAPI failure, missing handler
    [InlineData(2)] // bad arguments
    [InlineData(5)] // not an administrator
    [InlineData(-1)]
    [InlineData(-1073741819)] // access violation: the exe crashed
    [InlineData(int.MaxValue)]
    public void AnyOtherNonZeroCode_AbortsBeforeAnythingIsDeleted(int code)
    {
        SetupException ex = Fail(code);

        Assert.Contains($"code {code}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--recover", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was removed", ex.Message, StringComparison.Ordinal);
    }
}
