using Blocker.Sdk.Session;
using Blocker.UI.Agent;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class TrayModelTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Tooltip_counts_down_with_the_local_clock()
    {
        SessionTimeline timeline = Timeline(Snapshots.Active(TimeSpan.FromMinutes(90)));
        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal("WindowsDetox - 01:29:30 remaining", TrayModel.Tooltip(timeline, connected: true));
    }

    [Fact]
    public void Tooltip_shows_pass_and_session_time_during_an_emergency_pass()
    {
        SessionTimeline timeline = Timeline(Snapshots.Pass(TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(5)));

        Assert.Equal("WindowsDetox - emergency pass 00:05:00, session 00:45:00 remaining", TrayModel.Tooltip(timeline, connected: true));
    }

    [Fact]
    public void Tooltip_without_a_session_says_so() =>
        Assert.Equal("WindowsDetox - no session", TrayModel.Tooltip(Timeline(SessionSnapshot.Idle), connected: true));

    [Fact]
    public void Tooltip_reports_a_missing_connection_before_anything_else() =>
        Assert.Equal("WindowsDetox - service not connected", TrayModel.Tooltip(Timeline(Snapshots.Active(TimeSpan.FromHours(1))), connected: false));

    [Fact]
    public void Tooltip_fits_the_shell_limit()
    {
        SessionTimeline timeline = Timeline(Snapshots.Pass(TimeSpan.FromDays(400), TimeSpan.FromDays(400)));

        Assert.True(TrayModel.Tooltip(timeline, connected: true).Length <= TrayModel.MaxTooltipLength);
    }

    [Fact]
    public void Menu_offers_exit_only_while_no_session_runs()
    {
        Assert.Equal([TrayCommand.Open, TrayCommand.Exit], TrayModel.Menu(SessionSnapshot.Idle).Select(i => i.Command));
        Assert.Equal([TrayCommand.Open], TrayModel.Menu(Snapshots.Active(TimeSpan.FromHours(1))).Select(i => i.Command));
        Assert.Equal([TrayCommand.Open], TrayModel.Menu(Snapshots.Pass(TimeSpan.FromHours(1), TimeSpan.FromMinutes(5))).Select(i => i.Command));
    }

    [Fact]
    public void Menu_always_starts_with_open_blocker() =>
        Assert.Equal("Open Blocker", TrayModel.Menu(Snapshots.Active(TimeSpan.FromHours(1)))[0].Text);

    private SessionTimeline Timeline(SessionSnapshot snapshot)
    {
        SessionTimeline timeline = new(_time);
        timeline.Update(snapshot);
        return timeline;
    }
}
