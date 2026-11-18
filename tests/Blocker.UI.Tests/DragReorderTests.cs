using Blocker.UI.Views;

namespace Blocker.UI.Tests;

public sealed class DragReorderTests
{
    private static DragReorder Started()
    {
        DragReorder reorder = new();
        reorder.Begin(100, 100);
        return reorder;
    }

    [Fact]
    public void Nothing_under_the_pointer_stays_put() =>
        Assert.Equal(-1, Started().TargetIndex(dragged: 0, hit: -1, x: 400, y: 100));

    [Fact]
    public void Hovering_the_dragged_widgets_own_slot_stays_put() =>
        Assert.Equal(-1, Started().TargetIndex(dragged: 2, hit: 2, x: 100, y: 100));

    [Fact]
    public void The_first_swap_is_allowed_right_away() =>
        Assert.Equal(1, Started().TargetIndex(dragged: 0, hit: 1, x: 105, y: 100));

    [Fact]
    public void After_a_swap_the_pointer_must_travel_before_swapping_again()
    {
        DragReorder reorder = Started();
        reorder.Swapped(300, 100);

        Assert.Equal(-1, reorder.TargetIndex(dragged: 1, hit: 0, x: 300, y: 100));
        Assert.Equal(-1, reorder.TargetIndex(dragged: 1, hit: 0, x: 300 + DragReorder.SwapDistance, y: 100));
        Assert.Equal(0, reorder.TargetIndex(dragged: 1, hit: 0, x: 300 + DragReorder.SwapDistance + 1, y: 100));
    }

    [Fact]
    public void Distance_is_measured_in_both_axes()
    {
        DragReorder reorder = Started();
        reorder.Swapped(0, 0);

        Assert.Equal(-1, reorder.TargetIndex(dragged: 1, hit: 0, x: 16, y: 16));
        Assert.Equal(0, reorder.TargetIndex(dragged: 1, hit: 0, x: 18, y: 18));
    }

    [Fact]
    public void Re_entering_the_dragged_slot_allows_the_next_swap_at_once()
    {
        DragReorder reorder = Started();
        reorder.Swapped(300, 100);

        Assert.Equal(-1, reorder.TargetIndex(dragged: 1, hit: 1, x: 300, y: 100));
        Assert.Equal(2, reorder.TargetIndex(dragged: 1, hit: 2, x: 301, y: 100));
    }
}
