namespace Blocker.UI.Controls.Tests;

public sealed class MotionTests
{
    [Fact]
    public void Easing_curves_start_at_zero_and_end_at_one()
    {
        Assert.Equal(0, Motion.EaseOut(0));
        Assert.Equal(1, Motion.EaseOut(1));
        Assert.Equal(0, Motion.EaseIn(0));
        Assert.Equal(1, Motion.EaseIn(1));
    }

    [Fact]
    public void Ease_out_leads_and_ease_in_lags()
    {
        Assert.True(Motion.EaseOut(0.5) > 0.5);
        Assert.True(Motion.EaseIn(0.5) < 0.5);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void Easing_clamps_progress_so_it_never_overshoots(double t, double expected)
    {
        Assert.Equal(expected, Motion.EaseOut(t));
        Assert.Equal(expected, Motion.EaseIn(t));
    }

    [Fact]
    public void Easing_never_decreases()
    {
        double outPrevious = 0;
        double inPrevious = 0;
        for (double t = 0; t <= 1; t += 0.05)
        {
            Assert.InRange(Motion.EaseOut(t), outPrevious, 1);
            Assert.InRange(Motion.EaseIn(t), inPrevious, 1);
            (outPrevious, inPrevious) = (Motion.EaseOut(t), Motion.EaseIn(t));
        }
    }

    [Fact]
    public void Durations_are_ordered_and_restrained()
    {
        Assert.True(Motion.Fast < Motion.Exit);
        Assert.True(Motion.Exit < Motion.Normal);
        Assert.True(Motion.Normal < Motion.Emphasis);
        Assert.True(Motion.Emphasis <= TimeSpan.FromMilliseconds(250));
        Assert.True(Motion.LiftScale <= 1.03);
        Assert.InRange(Motion.PanelOffset, 16, 24);
        Assert.InRange(Motion.FlyoutOffset, 4, 12);
        Assert.InRange(Motion.ScrimOpacity, 0, 0.5);
    }
}
