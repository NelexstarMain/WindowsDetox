using Blocker.Contracts;
using Blocker.Sdk.Session;
using Blocker.UI.Controls.Services;
using Blocker.UI.Services;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class SetupViewModelTests
{
    private readonly FakeServiceConnection _connection = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeAppDialogs _appDialogs = new();
    private readonly FakeSnackbarService _snackbar = new();
    private readonly FakeTicker _ticker = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero));
    private readonly Navigator _navigator = new();
    private readonly AllowlistViewModel _allowlist;

    public SetupViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _connection.On<ScanAppsResponse>(IpcMethods.AllowlistScanApps, () => new ScanAppsResponse(
            [
                new InstalledAppDto("Firefox", @"C:\f.exe", "bb", null, null, AppSourceDto.StartMenu),
                new InstalledAppDto("Notepad", @"C:\n.exe", "cc", null, null, AppSourceDto.StartMenu),
            ]));
        _connection.On<AllowlistDto>(IpcMethods.AllowlistGet, () => new AllowlistDto([], []));
        _allowlist = new AllowlistViewModel(_connection, _snackbar);
        _allowlist.FilteredApps.First(a => a.DisplayName == "Notepad").IsSelected = true;
        _allowlist.NewDomain = "example.com";
        _allowlist.AddDomainCommand.Execute(null);
    }

    private SetupViewModel Create() => new(_connection, _allowlist, _dialogs, _appDialogs, _snackbar, _navigator, _ticker, _time);

    [Theory]
    [InlineData(0, 60)]
    [InlineData(1, 120)]
    [InlineData(2, 300)]
    [InlineData(3, 1440)]
    [InlineData(4, 2880)]
    public void Presets_map_to_minutes(int index, int minutes)
    {
        SetupViewModel vm = Create();

        vm.SelectedDuration = vm.DurationOptions[index];

        Assert.Equal(minutes, vm.DurationMinutes);
        Assert.False(vm.IsCustomDuration);
        Assert.Null(vm.DurationError);
    }

    [Fact]
    public void Custom_duration_combines_hours_and_minutes()
    {
        SetupViewModel vm = Create();

        vm.SelectedDuration = vm.DurationOptions[^1];
        vm.CustomHours = 3;
        vm.CustomMinutes = 25;

        Assert.True(vm.IsCustomDuration);
        Assert.Equal(205, vm.DurationMinutes);
        Assert.Equal("3 h 25 min", vm.DurationText);
    }

    [Theory]
    [InlineData(0, 10, "at least 15 minutes")]
    [InlineData(721, 0, "30 days or less")]
    public void Custom_duration_outside_the_allowed_range_is_an_error_and_blocks_start(double hours, double minutes, string fragment)
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[^1];

        vm.CustomHours = hours;
        vm.CustomMinutes = minutes;

        Assert.Contains(fragment, vm.DurationError);
        Assert.False(vm.CanStart);
        Assert.False(vm.StartSessionCommand.CanExecute(null));
    }

    [Fact]
    public void Request_carries_duration_passes_carry_over_and_the_allowlist()
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[2];
        vm.PassesPerDay = 3;
        vm.PassMinutes = 7;
        vm.CarryOverUnusedPasses = true;

        StartSessionRequest request = vm.BuildRequest();

        Assert.Equal(300, request.DurationMinutes);
        Assert.Equal(3, request.EmergencyPassesPerDay);
        Assert.Equal(7, request.EmergencyPassDurationMinutes);
        Assert.True(request.CarryOverUnusedPasses);
        Assert.Equal(["Notepad"], request.Apps.Select(a => a.DisplayName));
        Assert.Equal(["example.com"], request.Domains);
    }

    [Theory]
    [InlineData(11, 10, "between 0 and 10")]
    [InlineData(2, 0, "1 to 30")]
    [InlineData(2, 31, "1 to 30")]
    public void Pass_values_outside_the_service_limits_block_start(double passes, double minutes, string fragment)
    {
        SetupViewModel vm = Create();

        vm.PassesPerDay = passes;
        vm.PassMinutes = minutes;

        Assert.Contains(fragment, vm.PassError);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public void Summary_describes_ends_time_apps_domains_and_passes()
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[1];

        Assert.Equal("2 h", vm.DurationText);
        Assert.Equal("Today at 22:00", vm.EndsAtText);
        Assert.Equal("1 app allowed", vm.AppsText);
        Assert.Equal("1 web domain allowed", vm.DomainsText);
        Assert.Equal("2 passes a day, 10 min each", vm.PassesText);

        vm.SelectedDuration = vm.DurationOptions[3];
        Assert.Equal("Tomorrow at 20:00", vm.EndsAtText);

        vm.PassesPerDay = 0;
        Assert.Equal("No emergency passes", vm.PassesText);
    }

    [Fact]
    public void Ends_time_follows_the_clock()
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[0];
        Assert.Equal("Today at 21:00", vm.EndsAtText);

        _time.Advance(TimeSpan.FromMinutes(30));
        _ticker.Raise();

        Assert.Equal("Today at 21:30", vm.EndsAtText);
    }

    [Fact]
    public void Start_is_blocked_while_disconnected_or_while_a_session_runs()
    {
        SetupViewModel vm = Create();
        Assert.True(vm.CanStart);

        _connection.SetConnected(false);
        Assert.False(vm.CanStart);
        Assert.Equal("Waiting for the Blocker service.", vm.StartBlockedReason);

        _connection.SetConnected(true);
        Assert.True(vm.CanStart);

        _connection.Push(Snapshots.Active(TimeSpan.FromHours(1)));
        Assert.False(vm.CanStart);
        Assert.Equal("A session is already running.", vm.StartBlockedReason);
    }

    [Fact]
    public void Confirmation_lists_what_will_be_locked()
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[2];

        string message = vm.BuildConfirmationMessage();

        Assert.Contains("5 h", message);
        Assert.Contains("Every app except the 1 you allowed", message);
        Assert.Contains("Every website except 1 allowed domain", message);
        Assert.Contains("plugins", message);
        Assert.Contains("2 daily emergency passes", message);
    }

    [Fact]
    public async Task Start_confirms_calls_the_service_shows_the_code_once_and_opens_the_dashboard()
    {
        SetupViewModel vm = Create();
        vm.SelectedDuration = vm.DurationOptions[1];
        StartSessionRequest? sent = null;
        _connection.On<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, r =>
        {
            sent = r;
            return new StartSessionResponse(true, [], Guid.NewGuid(), "ABCD-1234");
        });
        AppPage? page = null;
        _navigator.NavigationRequested += (_, p) => page = p;

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal("Start session", _dialogs.Confirmations[0].Confirm);
        Assert.Equal(120, sent!.DurationMinutes);
        Assert.Equal(["ABCD-1234"], _appDialogs.ShownCodes);
        Assert.Equal(AppPage.Dashboard, page);
    }

    [Fact]
    public async Task Declined_confirmation_starts_nothing()
    {
        SetupViewModel vm = Create();
        _dialogs.ConfirmResult = false;

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal(0, _connection.CallCount(IpcMethods.SessionStart));
        Assert.Empty(_appDialogs.ShownCodes);
    }

    [Fact]
    public async Task Start_with_warnings_shows_them_without_blocking_the_dashboard()
    {
        SetupViewModel vm = Create();
        _connection.On<StartSessionRequest, StartSessionResponse>(
            IpcMethods.SessionStart,
            _ => new StartSessionResponse(true, [], Guid.NewGuid(), "ABCD-1234", ["Windows refused some extra protections: game.exe."]));
        AppPage? page = null;
        _navigator.NavigationRequested += (_, p) => page = p;

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal(AppPage.Dashboard, page);
        Assert.Empty(vm.StartErrors);
        Assert.Contains(_snackbar.Shown, s => s.Severity == NotificationSeverity.Warning && s.Message.Contains("game.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refused_start_shows_the_errors_and_never_a_code()
    {
        SetupViewModel vm = Create();
        _connection.On<StartSessionRequest, StartSessionResponse>(IpcMethods.SessionStart, _ => new StartSessionResponse(false, ["A session is already running."], null, null));
        AppPage? page = null;
        _navigator.NavigationRequested += (_, p) => page = p;

        await vm.StartSessionCommand.ExecuteAsync(null);

        Assert.Equal(["A session is already running."], vm.StartErrors);
        Assert.Empty(_appDialogs.ShownCodes);
        Assert.Null(page);
        Assert.Contains(_snackbar.Shown, s => s.Severity == NotificationSeverity.Error);
    }
}
