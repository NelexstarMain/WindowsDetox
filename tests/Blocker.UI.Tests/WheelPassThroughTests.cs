using Blocker.UI.Behaviors;

namespace Blocker.UI.Tests;

public sealed class WheelPassThroughTests
{
    private const int Up = 120;
    private const int Down = -120;

    [Fact]
    public void A_scroller_without_extent_cannot_consume_either_direction()
    {
        Assert.False(WheelPassThrough.CanConsume(0, 0, Up));
        Assert.False(WheelPassThrough.CanConsume(0, 0, Down));
    }

    [Fact]
    public void Layout_rounding_residue_is_not_an_extent()
    {
        Assert.False(WheelPassThrough.CanConsume(0, 0.3, Down));
    }

    [Theory]
    [InlineData(0, 300, Down, true)]
    [InlineData(150, 300, Down, true)]
    [InlineData(300, 300, Down, false)]
    [InlineData(0, 300, Up, false)]
    [InlineData(150, 300, Up, true)]
    [InlineData(300, 300, Up, true)]
    public void A_scroller_consumes_only_while_it_can_move_in_the_wheel_direction(double offset, double scrollable, int delta, bool expected)
    {
        Assert.Equal(expected, WheelPassThrough.CanConsume(offset, scrollable, delta));
    }

    [Fact]
    public void A_zero_delta_is_never_consumed()
    {
        Assert.False(WheelPassThrough.CanConsume(100, 300, 0));
    }
}
