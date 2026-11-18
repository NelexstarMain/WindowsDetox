using Blocker.UI.Views;

namespace Blocker.UI.Tests;

public sealed class HoverIntentTests
{
    private static readonly object A = new();
    private static readonly object B = new();

    private static HoverIntent<object> Create() => new(TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(250));

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Pointing_counts_only_after_the_open_delay()
    {
        HoverIntent<object> intent = Create();

        intent.Point(A, Ms(0));

        Assert.True(intent.IsPending);
        Assert.False(intent.Update(Ms(119)));
        Assert.Null(intent.Current);
        Assert.True(intent.Update(Ms(120)));
        Assert.Same(A, intent.Current);
        Assert.False(intent.IsPending);
    }

    [Fact]
    public void Leaving_counts_only_after_the_close_delay()
    {
        HoverIntent<object> intent = Create();
        intent.Force(A);

        intent.Point(null, Ms(1000));

        Assert.False(intent.Update(Ms(1249)));
        Assert.Same(A, intent.Current);
        Assert.True(intent.Update(Ms(1250)));
        Assert.Null(intent.Current);
    }

    [Fact]
    public void Crossing_from_an_item_into_its_flyout_keeps_it_open()
    {
        HoverIntent<object> intent = Create();
        intent.Force(A);

        intent.Point(null, Ms(1000));
        intent.Point(A, Ms(1100));

        Assert.False(intent.IsPending);
        Assert.False(intent.Update(Ms(2000)));
        Assert.Same(A, intent.Current);
    }

    [Fact]
    public void Brushing_past_an_item_does_not_open_it()
    {
        HoverIntent<object> intent = Create();

        intent.Point(A, Ms(0));
        intent.Point(null, Ms(60));

        Assert.False(intent.IsPending);
        Assert.False(intent.Update(Ms(500)));
        Assert.Null(intent.Current);
    }

    [Fact]
    public void Moving_to_another_item_switches_after_the_open_delay_of_the_new_one()
    {
        HoverIntent<object> intent = Create();
        intent.Force(A);

        intent.Point(null, Ms(1000));
        intent.Point(B, Ms(1010));

        Assert.False(intent.Update(Ms(1100)));
        Assert.Same(A, intent.Current);
        Assert.True(intent.Update(Ms(1130)));
        Assert.Same(B, intent.Current);
    }

    [Fact]
    public void Forcing_counts_at_once_and_cancels_anything_pending()
    {
        HoverIntent<object> intent = Create();
        intent.Point(A, Ms(0));

        intent.Force(B);

        Assert.Same(B, intent.Current);
        Assert.False(intent.IsPending);
        Assert.False(intent.Update(Ms(1000)));
        Assert.Same(B, intent.Current);
    }
}
