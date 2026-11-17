using Blocker.Sdk.Extensions;
using Blocker.Sdk.Session;

namespace Blocker.SDK.Tests;

public class DecisionAndSnapshotTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Block_BlankReason_ThrowsArgumentException(string reason) =>
        Assert.Throws<ArgumentException>(() => InterceptDecision.Block(reason));

    [Fact]
    public void Block_NullReason_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => InterceptDecision.Block(null!));

    [Fact]
    public void Block_ValidReason_ReturnsBlockVerdictWithReason()
    {
        InterceptDecision decision = InterceptDecision.Block("Games are off limits");

        Assert.Equal(InterceptVerdict.Block, decision.Verdict);
        Assert.Equal("Games are off limits", decision.Reason);
    }

    [Fact]
    public void Abstain_Always_HasAbstainVerdictAndNoReason()
    {
        Assert.Equal(InterceptVerdict.Abstain, InterceptDecision.Abstain.Verdict);
        Assert.Null(InterceptDecision.Abstain.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Veto_BlankReason_ThrowsArgumentException(string reason) =>
        Assert.Throws<ArgumentException>(() => LockVote.Veto(reason));

    [Fact]
    public void Veto_NullReason_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() => LockVote.Veto(null!));

    [Fact]
    public void Veto_ValidReason_IsVetoWithReason()
    {
        LockVote vote = LockVote.Veto("Session in progress");

        Assert.True(vote.IsVeto);
        Assert.Equal("Session in progress", vote.Reason);
    }

    [Fact]
    public void Allow_Always_IsNotVetoAndHasNoReason()
    {
        Assert.False(LockVote.Allow.IsVeto);
        Assert.Null(LockVote.Allow.Reason);
    }

    [Fact]
    public void Idle_Always_HasNoSessionAndZeroedValues()
    {
        SessionSnapshot idle = SessionSnapshot.Idle;

        Assert.Equal(SessionPhase.Idle, idle.Phase);
        Assert.Null(idle.SessionId);
        Assert.Null(idle.Remaining);
        Assert.Null(idle.EmergencyPassRemaining);
        Assert.Equal(0, idle.EmergencyPassesLeftToday);
        Assert.Equal(0, idle.EmergencyPassesPerDay);
        Assert.Equal(TimeSpan.Zero, idle.EmergencyPassDuration);
        Assert.False(idle.IsSessionRunning);
    }

    [Theory]
    [InlineData(SessionPhase.Idle, false)]
    [InlineData(SessionPhase.Active, true)]
    [InlineData(SessionPhase.EmergencyPass, true)]
    [InlineData((SessionPhase)99, false)]
    public void IsSessionRunning_Phase_ReturnsExpected(SessionPhase phase, bool expected)
    {
        var snapshot = SessionSnapshot.Idle with { Phase = phase };

        Assert.Equal(expected, snapshot.IsSessionRunning);
    }
}
