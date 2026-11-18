using Blocker.UI.Views;

namespace Blocker.UI.Tests;

public sealed class ReflowAnimatorTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(100);

    private static ReflowAnimator<string> Create(TimeSpan? duration = null) => new(duration ?? Duration, t => t);

    private static Box At(double x, double y = 0, double width = 100, double height = 100) => new(x, y, width, height);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void A_new_item_appears_in_place_without_moving()
    {
        ReflowAnimator<string> animator = Create();

        Box shown = animator.Retarget("a", At(40), Ms(0), animate: true);

        Assert.Equal(At(40), shown);
        Assert.True(animator.Contains("a"));
        Assert.False(animator.IsAnimating(Ms(0)));
    }

    [Fact]
    public void A_new_target_slides_there_over_the_duration()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);

        Box start = animator.Retarget("a", At(100), Ms(10), animate: true);

        Assert.Equal(At(0), start);
        Assert.True(animator.IsAnimating(Ms(10)));
        Assert.Equal(50, animator.Current("a", Ms(60)).X, 6);
        Assert.Equal(At(100), animator.Current("a", Ms(110)));
        Assert.False(animator.IsAnimating(Ms(110)));
    }

    [Fact]
    public void A_target_that_changes_mid_flight_continues_from_where_the_item_is_drawn()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Retarget("a", At(100), Ms(0), animate: true);

        Box redirected = animator.Retarget("a", At(200), Ms(50), animate: true);

        Assert.Equal(50, redirected.X, 6);
        Assert.Equal(125, animator.Current("a", Ms(100)).X, 6);
        Assert.Equal(200, animator.Current("a", Ms(150)).X, 6);
    }

    [Fact]
    public void Asking_for_the_same_target_again_does_not_restart_the_slide()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Retarget("a", At(100), Ms(0), animate: true);

        Box again = animator.Retarget("a", At(100), Ms(50), animate: true);

        Assert.Equal(50, again.X, 6);
        Assert.Equal(75, animator.Current("a", Ms(75)).X, 6);
        Assert.Equal(100, animator.Current("a", Ms(100)).X, 6);
    }

    [Fact]
    public void Without_animation_the_item_jumps_and_any_slide_is_dropped()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Retarget("a", At(100), Ms(0), animate: true);

        Box jumped = animator.Retarget("a", At(300), Ms(20), animate: false);

        Assert.Equal(At(300), jumped);
        Assert.False(animator.IsAnimating(Ms(20)));
    }

    [Fact]
    public void A_zero_duration_makes_every_move_instant()
    {
        ReflowAnimator<string> animator = Create(TimeSpan.Zero);
        animator.Retarget("a", At(0), Ms(0), animate: true);

        Box moved = animator.Retarget("a", At(100), Ms(0), animate: true);

        Assert.Equal(At(100), moved);
        Assert.False(animator.IsAnimating(Ms(0)));
    }

    [Fact]
    public void Size_changes_are_animated_like_moves()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0, 0, 100, 100), Ms(0), animate: true);
        animator.Retarget("a", At(0, 0, 200, 300), Ms(0), animate: true);

        Box half = animator.Current("a", Ms(50));

        Assert.Equal((150, 200), (half.Width, half.Height));
    }

    [Fact]
    public void A_released_item_slides_from_where_it_was_held()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Hold("a", At(80), Ms(10));
        Assert.False(animator.IsAnimating(Ms(10)));

        Box start = animator.Retarget("a", At(0), Ms(20), animate: true);

        Assert.Equal(At(80), start);
        Assert.Equal(40, animator.Current("a", Ms(70)).X, 6);
    }

    [Fact]
    public void The_curve_shapes_the_progress()
    {
        ReflowAnimator<string> animator = new(Duration, t => t * t);
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Retarget("a", At(100), Ms(0), animate: true);

        Assert.Equal(25, animator.Current("a", Ms(50)).X, 6);
    }

    [Fact]
    public void Pruned_items_are_forgotten()
    {
        ReflowAnimator<string> animator = Create();
        animator.Retarget("a", At(0), Ms(0), animate: true);
        animator.Retarget("b", At(100), Ms(0), animate: true);

        animator.Prune(["b"]);

        Assert.False(animator.Contains("a"));
        Assert.True(animator.Contains("b"));
    }
}
