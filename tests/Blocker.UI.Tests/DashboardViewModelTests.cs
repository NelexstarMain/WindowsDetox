using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class DashboardViewModelTests
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeAppDialogs _appDialogs = new();
    private readonly FakeSnackbarService _snackbar = new();
    private readonly FakeTicker _ticker = new();
    private readonly FakeTimeProvider _time = new();
    private readonly Navigator _navigator = new();

    private DashboardViewModel Create() => new(_connection, _dialogs, _appDialogs, _snackbar, _navigator, _ticker, _time, new WidgetBoardViewModel(new InMemoryWidgetLayoutStore(), _navigator));

    [Fact]
    public void Idle_shows_idle_chip_no_clock_and_disabled_emergency_exit()
    {
        DashboardViewModel vm = Create();

        Assert.Equal("Idle", vm.StateLabel);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsSessionRunning);
        Assert.Equal("--:--:--", vm.ClockText);
        Assert.False(vm.UseEmergencyPassCommand.CanExecute(null));
        Assert.Contains("while a session is running", vm.EmergencyHint);
    }

    [Fact]
    public void Active_session_counts_down_between_pushes()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(2)));

        Assert.Equal("Active", vm.StateLabel);
        Assert.Equal("02:00:00", vm.ClockText);

        _time.Advance(TimeSpan.FromSeconds(61));
        _ticker.Raise();

        Assert.Equal("01:58:59", vm.ClockText);
    }

    [Fact]
    public void State_transitions_follow_pushed_snapshots()
    {
        DashboardViewModel vm = Create();

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        Assert.Equal("Active", vm.StateLabel);
        Assert.False(vm.IsPassActive);

        _connection.Push(Snapshots.Pass(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10)));
        Assert.Equal("Emergency pass", vm.StateLabel);
        Assert.True(vm.IsPassActive);
        Assert.Equal("00:10:00", vm.PassClockText);
        Assert.False(vm.IsPassWarning);

        _connection.Push(Snapshots.Active(TimeSpan.FromMinutes(50), passesLeft: 1));
        Assert.Equal("Active", vm.StateLabel);
        Assert.False(vm.IsPassActive);

        _connection.Push(SessionSnapshot.Idle);
        Assert.Equal("Idle", vm.StateLabel);
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public void Pass_enters_warning_state_in_its_last_minute()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Pass(TimeSpan.FromHours(1), TimeSpan.FromSeconds(75)));
        Assert.False(vm.IsPassWarning);

        _time.Advance(TimeSpan.FromSeconds(15));
        _ticker.Raise();

        Assert.True(vm.IsPassWarning);
        Assert.Equal("00:01:00", vm.PassClockText);
    }

    [Fact]
    public void Progress_ratio_tracks_elapsed_fraction_and_resets_when_idle()
    {
        DashboardViewModel vm = Create();
        Assert.Equal(0, vm.ProgressRatio);

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        Assert.Equal(0, vm.ProgressRatio);

        _time.Advance(TimeSpan.FromMinutes(15));
        _ticker.Raise();
        Assert.Equal(0.25, vm.ProgressRatio, 3);

        _connection.Push(SessionSnapshot.Idle);
        Assert.Equal(0, vm.ProgressRatio);
    }

    [Fact]
    public void Open_allowlist_requests_the_allowlist_page()
    {
        DashboardViewModel vm = Create();
        AppPage? requested = null;
        _navigator.NavigationRequested += (_, page) => requested = page;

        vm.OpenAllowlistCommand.Execute(null);

        Assert.Equal(AppPage.Allowlist, requested);
    }

    [Theory]
    [InlineData(true, SessionPhase.Active, 1, true)]
    [InlineData(true, SessionPhase.Active, 0, false)]
    [InlineData(true, SessionPhase.Idle, 2, false)]
    [InlineData(true, SessionPhase.EmergencyPass, 1, false)]
    [InlineData(false, SessionPhase.Active, 1, false)]
    public void Emergency_exit_is_available_only_when_connected_active_and_passes_remain(bool connected, SessionPhase phase, int passesLeft, bool expected)
    {
        DashboardViewModel vm = Create();
        _connection.Push(new SessionSnapshot(phase, Guid.NewGuid(), TimeSpan.FromHours(1), phase == SessionPhase.EmergencyPass ? TimeSpan.FromMinutes(5) : null, passesLeft, 2, TimeSpan.FromMinutes(10)));
        _connection.SetConnected(connected);

        Assert.Equal(expected, vm.CanUseEmergencyPass);
        Assert.Equal(expected, vm.UseEmergencyPassCommand.CanExecute(null));
        Assert.Equal(expected, vm.EmergencyHint is null);
    }

    [Fact]
    public async Task Declining_the_confirmation_does_not_call_the_service()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        _dialogs.ConfirmResult = false;

        await vm.UseEmergencyPassCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(0, _connection.CallCount(IpcMethods.SessionUseEmergencyPass));
    }

    [Fact]
    public async Task Confirmed_emergency_exit_calls_the_service_and_adopts_the_returned_status()
    {
        DashboardViewModel vm = Create();
        SessionSnapshot active = Snapshots.Active(TimeSpan.FromHours(1));
        _connection.Push(active);
        SessionSnapshot afterPass = Snapshots.Pass(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), passesLeft: 1);
        _connection.On<EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, () => new EmergencyPassResponse(EmergencyPassOutcome.Started, Snapshots.Dto(afterPass)));

        await vm.UseEmergencyPassCommand.ExecuteAsync(null);

        Assert.Equal(1, _connection.CallCount(IpcMethods.SessionUseEmergencyPass));
        Assert.Equal("Emergency pass", vm.StateLabel);
        Assert.True(vm.IsPassActive);
        Assert.Contains(_snackbar.Shown, s => s.Title == "Emergency pass started" && s.Severity == NotificationSeverity.Warning);
    }

    [Fact]
    public async Task Service_failure_during_emergency_exit_is_reported_in_a_snackbar()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        _connection.On<EmergencyPassResponse>(IpcMethods.SessionUseEmergencyPass, () => throw new IpcException(IpcErrorCode.RateLimited, "slow down"));

        await vm.UseEmergencyPassCommand.ExecuteAsync(null);

        Assert.Contains(_snackbar.Shown, s => s.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task Break_glass_sends_the_trimmed_code_and_reports_success()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        _appDialogs.CodeToEnter = "  ABCD-EFGH  ";
        _connection.On<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, r => new BreakGlassResponse(r.Code == "ABCD-EFGH"));

        await vm.EnterBreakGlassCodeCommand.ExecuteAsync(null);

        Assert.Contains(_snackbar.Shown, s => s.Title == "Session ended" && s.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task Wrong_break_glass_code_reveals_nothing_beyond_a_generic_failure()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        _appDialogs.CodeToEnter = "wrong";
        _connection.On<BreakGlassRequest, BreakGlassResponse>(IpcMethods.SessionBreakGlass, _ => new BreakGlassResponse(false));

        await vm.EnterBreakGlassCodeCommand.ExecuteAsync(null);

        Assert.Contains(_snackbar.Shown, s => s.Title == "Code not accepted" && s.Severity == NotificationSeverity.Error);
    }

    [Fact]
    public async Task Cancelled_break_glass_prompt_makes_no_call()
    {
        DashboardViewModel vm = Create();
        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        _appDialogs.CodeToEnter = null;

        await vm.EnterBreakGlassCodeCommand.ExecuteAsync(null);

        Assert.Equal(0, _connection.CallCount(IpcMethods.SessionBreakGlass));
    }

    [Fact]
    public void Start_session_navigates_to_setup()
    {
        DashboardViewModel vm = Create();
        AppPage? requested = null;
        _navigator.NavigationRequested += (_, page) => requested = page;

        vm.StartSessionCommand.Execute(null);

        Assert.Equal(AppPage.Setup, requested);
    }

    [Fact]
    public async Task Widgets_come_from_valid_trees_only_and_actions_reach_the_service()
    {
        Blocker.UI.Sdk.UiNode good = new Blocker.UI.Sdk.UiCard([new Blocker.UI.Sdk.UiText("hello")], "Focus");
        _connection.On<GetContributionsRequest, GetContributionsResponse>(
            IpcMethods.UiGetContributions,
            r => new GetContributionsResponse(r.Slot,
            [
                new UiContributionDto("a.plugin", "w1", null, Blocker.UI.Sdk.UiJson.Serialize(good)),
                new UiContributionDto("b.plugin", "w2", null, "{ not json"),
            ]));
        _connection.On<SendActionRequest, EmptyDto>(IpcMethods.UiSendAction, _ => new EmptyDto());
        DashboardViewModel vm = Create();

        await vm.RefreshWidgetsCommand.ExecuteAsync(null);

        ContributionViewModel widget = Assert.Single(vm.Widgets);
        Assert.Equal("a.plugin/w1", widget.Key);

        await vm.SendWidgetActionCommand.ExecuteAsync(new ContributionAction(widget, Blocker.UI.Sdk.UiAction.Click("go")));

        Assert.Equal(1, _connection.CallCount(IpcMethods.UiSendAction));
    }
}
