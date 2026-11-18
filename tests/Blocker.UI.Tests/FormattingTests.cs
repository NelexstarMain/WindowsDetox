using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class FormattingTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(1, "00:00:01")]
    [InlineData(59.2, "00:01:00")]
    [InlineData(3661.2, "01:01:02")]
    [InlineData(172800, "48:00:00")]
    [InlineData(-5, "00:00:00")]
    public void Clock_shows_hours_minutes_seconds_and_rounds_partial_seconds_up(double seconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Clock(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Clock_without_a_value_shows_placeholder() => Assert.Equal("--:--:--", DurationFormat.Clock(null));

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    [InlineData(300, "5 h")]
    [InlineData(1440, "1 day")]
    [InlineData(2880, "2 days")]
    [InlineData(1500, "25 h")]
    public void Compact_picks_the_shortest_natural_unit(int minutes, string expected) =>
        Assert.Equal(expected, DurationFormat.Compact(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Timeline_counts_down_from_the_moment_a_snapshot_arrived_and_never_goes_negative()
    {
        FakeTimeProvider time = new();
        SessionTimeline timeline = new(time);
        timeline.Update(Snapshots.Active(TimeSpan.FromSeconds(30)));

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(20), timeline.Remaining);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.Zero, timeline.Remaining);
    }

    [Fact]
    public void Timeline_restarts_counting_when_the_service_pushes_a_new_snapshot()
    {
        FakeTimeProvider time = new();
        SessionTimeline timeline = new(time);
        timeline.Update(Snapshots.Active(TimeSpan.FromMinutes(10)));
        time.Advance(TimeSpan.FromMinutes(3));

        timeline.Update(Snapshots.Active(TimeSpan.FromMinutes(6)));

        Assert.Equal(TimeSpan.FromMinutes(6), timeline.Remaining);
    }

    [Fact]
    public void Toast_countdown_shows_minutes_and_seconds_and_expires_one_second_after_zero()
    {
        FakeTimeProvider time = new();
        ToastViewModel toast = new(new Blocker.Contracts.NotificationDto("Pass ending", "Save your work", Blocker.Contracts.NotificationLevel.Warning), TimeSpan.FromSeconds(60), time);
        Assert.Equal("1:00", toast.CountdownText);
        Assert.True(toast.HasCountdown);
        Assert.True(toast.IsWarning);

        time.Advance(TimeSpan.FromSeconds(18));
        toast.Refresh();
        Assert.Equal("0:42", toast.CountdownText);
        Assert.False(toast.IsExpired);

        time.Advance(TimeSpan.FromSeconds(43));
        toast.Refresh();
        Assert.Equal("0:00", toast.CountdownText);
        Assert.True(toast.IsExpired);
    }

    [Fact]
    public void Plain_toast_has_no_countdown_and_expires_after_its_default_lifetime()
    {
        FakeTimeProvider time = new();
        ToastViewModel toast = new(new Blocker.Contracts.NotificationDto("Hello", "World", Blocker.Contracts.NotificationLevel.Info), null, time);
        Assert.False(toast.HasCountdown);
        Assert.False(toast.IsWarning);

        time.Advance(ToastViewModel.DefaultLifetime);
        toast.Refresh();

        Assert.True(toast.IsExpired);
    }
}
