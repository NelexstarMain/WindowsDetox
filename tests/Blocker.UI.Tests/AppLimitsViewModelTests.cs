using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.UI.Tests;

public sealed class AppLimitsViewModelTests
{
    private static readonly AllowlistEntryDto Discord = new("Discord", @"C:\d.exe", "dd", null);
    private static readonly AllowlistEntryDto Spotify = new("Spotify", @"C:\s.exe", "ss", null);
    private static readonly AllowlistEntryDto Notepad = new("Notepad", @"C:\n.exe", "nn", null);

    private static readonly AppBudgetDto NoBudget = new(AppBudgetMode.None, 0, null, 0);

    private readonly FakeServiceConnection _connection = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeSnackbarService _snackbar = new();

    // Monday 2026-11-23, 10:30 in the local zone.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 10, 30, 0, TimeSpan.Zero));

    private List<AppRuleDto> _rules =
    [
        new(Discord, [new WeeklyRangeDto(0, 540, 1020)], NoBudget),
        new(Spotify, [], new AppBudgetDto(AppBudgetMode.Daily, 60, null, 0)),
    ];

    private List<AppRuleStatusDto> _statuses = [];
    private List<AppRulesDto> _savedRules = [];
    private OperationResponse _setResponse = new(true, []);

    public AppLimitsViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _statuses =
        [
            new(Discord, AppRuleState.ScheduledBlock, 0, null, new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.Zero)),
            new(Spotify, AppRuleState.Allowed, 42 * 60, 18 * 60, null),
        ];
    }

    private async Task<AppLimitsViewModel> CreateAsync()
    {
        _connection.On<ScanAppsResponse>(IpcMethods.AllowlistScanApps, () => new ScanAppsResponse(
            [
                new InstalledAppDto("Discord", Discord.ExecutablePath, "dd", null, null, AppSourceDto.StartMenu),
                new InstalledAppDto("Notepad", Notepad.ExecutablePath, "nn", null, null, AppSourceDto.StartMenu),
                new InstalledAppDto("Spotify", Spotify.ExecutablePath, "ss", null, null, AppSourceDto.StartMenu),
            ]));
        _connection.On<AppRulesDto>(IpcMethods.AppRulesGet, () => new AppRulesDto(_rules));
        _connection.On<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, () => new AppRulesStatusResponse(_statuses));
        _connection.On<AppRulesDto, OperationResponse>(IpcMethods.AppRulesSet, rules =>
        {
            _savedRules.Add(rules);
            if (_setResponse.Success)
            {
                _rules = [.. rules.Rules];
            }

            return _setResponse;
        });
        AppLimitsViewModel vm = new(_connection, _snackbar, _dialogs, _time);
        await vm.RefreshAsync(CancellationToken.None);
        return vm;
    }

    private static AppLimitItemViewModel Item(AppLimitsViewModel vm, string name) => vm.Items.First(i => i.App.DisplayName == name);

    private static UiAction Draw(int day, int start, int end) => Schedule(AppLimitsViewModel.GridId, day, start, end);

    private static UiAction Schedule(string id, int day, int start, int end) => new(
        id,
        UiActionKind.Schedule,
        null,
        new Dictionary<string, string> { ["day"] = day.ToString(System.Globalization.CultureInfo.InvariantCulture), ["start"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture), ["end"] = end.ToString(System.Globalization.CultureInfo.InvariantCulture) });

    private static UiTimeGridEvent[] Blocks(AppLimitsViewModel vm) => [.. ((UiTimeGrid)vm.ScheduleNode!).Events];

    [Fact]
    public async Task Apps_with_a_rule_are_pinned_on_top_and_grouped()
    {
        AppLimitsViewModel vm = await CreateAsync();

        Assert.Equal(["Discord", "Spotify", "Notepad"], vm.Items.Select(i => i.App.DisplayName));
        Assert.Equal([AppLimitItemViewModel.RuledGroup, AppLimitItemViewModel.RuledGroup, AppLimitItemViewModel.OtherGroup], vm.Items.Select(i => i.GroupName));
    }

    [Fact]
    public async Task Search_filters_the_list()
    {
        AppLimitsViewModel vm = await CreateAsync();

        vm.SearchText = "note";

        Assert.Equal(["Notepad"], vm.Items.Select(i => i.App.DisplayName));
    }

    [Fact]
    public async Task Each_app_shows_what_decides_about_it()
    {
        AppLimitsViewModel vm = await CreateAsync();

        Assert.Equal(LimitState.ScheduledBlock, Item(vm, "Discord").State);
        Assert.Equal("Blocked until 17:00", Item(vm, "Discord").StatusText);
        Assert.Equal(LimitState.Allowed, Item(vm, "Spotify").State);
        Assert.Equal("18 min left today", Item(vm, "Spotify").StatusText);
        Assert.Equal(0.7, Item(vm, "Spotify").UsageFraction, 3);
        Assert.True(Item(vm, "Spotify").HasPool);
        Assert.Equal(LimitState.None, Item(vm, "Notepad").State);
        Assert.False(Item(vm, "Notepad").HasPool);
    }

    [Fact]
    public async Task Polling_the_status_updates_the_list_without_losing_the_open_app_or_its_edits()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Spotify");
        vm.BudgetModeIndex = (int)AppBudgetMode.Weekly;

        _statuses = [_statuses[0], new AppRuleStatusDto(Spotify, AppRuleState.BudgetExhausted, 3600, 0, new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero))];
        await vm.PollStatusAsync();

        Assert.Equal(LimitState.PoolExhausted, Item(vm, "Spotify").State);
        Assert.Equal("Time is up until tomorrow 00:00", Item(vm, "Spotify").StatusText);
        Assert.Same(Item(vm, "Spotify"), vm.Editing);
        Assert.Equal((int)AppBudgetMode.Weekly, vm.BudgetModeIndex);
        Assert.True(vm.IsDirty);
        Assert.Equal(1.0, vm.UsageFraction);
    }

    [Fact]
    public async Task A_run_of_failed_polls_is_reported_once_and_reset_by_a_success()
    {
        AppLimitsViewModel vm = await CreateAsync();
        _connection.On<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, () => throw new IpcException(IpcErrorCode.NotFound, "gone"));

        await vm.PollStatusAsync();
        await vm.PollStatusAsync();
        Assert.Single(_snackbar.Shown);

        _connection.On<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, () => new AppRulesStatusResponse(_statuses));
        await vm.PollStatusAsync();
        _connection.On<AppRulesStatusResponse>(IpcMethods.AppRulesStatus, () => throw new IpcException(IpcErrorCode.NotFound, "gone"));
        await vm.PollStatusAsync();

        Assert.Equal(2, _snackbar.Shown.Count);
    }

    [Fact]
    public async Task Selecting_an_app_loads_its_blocks_and_pool()
    {
        AppLimitsViewModel vm = await CreateAsync();

        vm.SelectedItem = Item(vm, "Discord");

        Assert.True(vm.HasEditing);
        Assert.Equal([new UiTimeGridEvent("block-0", 0, 540, 1020, "Blocked", "09:00\u201317:00", UiTone.Accent, false, true, true)], Blocks(vm));
        Assert.Equal((int)AppBudgetMode.None, vm.BudgetModeIndex);
        Assert.False(vm.IsDirty);

        vm.SelectedItem = Item(vm, "Spotify");

        Assert.Empty(Blocks(vm));
        Assert.Equal((int)AppBudgetMode.Daily, vm.BudgetModeIndex);
        Assert.Equal((1.0, 0.0), (vm.DailyHours, vm.DailyMinutes));
        Assert.True(vm.HasUsage);
        Assert.Equal("42/60 min", vm.UsageText);
    }

    [Fact]
    public async Task Drawing_on_the_grid_adds_a_range_and_overlapping_draws_merge()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");

        vm.HandleGridAction(Draw(2, 540, 600));
        vm.HandleGridAction(Draw(2, 570, 660));

        UiTimeGridEvent block = Assert.Single(Blocks(vm));
        Assert.Equal((2, 540, 660), (block.Day, block.StartMinute, block.EndMinute));
        Assert.True(block.IsSelected);
        Assert.True(vm.IsDirty);
        Assert.Equal([new WeeklyRangeDto(2, 540, 660)], vm.BuildRule()!.BlockedRanges);
    }

    [Fact]
    public async Task Moving_or_resizing_a_block_rewrites_its_range()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Discord");

        vm.HandleGridAction(Schedule("block-0", 1, 600, 720));

        Assert.Equal([new WeeklyRangeDto(1, 600, 720)], vm.BuildRule()!.BlockedRanges);
    }

    [Fact]
    public async Task Invalid_grid_actions_are_ignored()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Discord");

        vm.HandleGridAction(Schedule("block-0", 9, 600, 720));
        vm.HandleGridAction(Schedule("block-7", 1, 600, 720));
        vm.HandleGridAction(new UiAction(AppLimitsViewModel.GridId, UiActionKind.Schedule, null, new Dictionary<string, string> { ["day"] = "x" }));

        Assert.False(vm.IsDirty);
        Assert.Equal([new WeeklyRangeDto(0, 540, 1020)], vm.BuildRule()!.BlockedRanges);
    }

    [Fact]
    public async Task Clicking_a_block_selects_it_and_remove_block_deletes_only_that_one()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");
        vm.HandleGridAction(Draw(0, 60, 120));
        vm.HandleGridAction(Draw(3, 60, 120));

        vm.HandleGridAction(UiAction.Click("block-0"));
        Assert.True(Blocks(vm)[0].IsSelected);
        Assert.False(Blocks(vm)[1].IsSelected);
        Assert.Equal("Mon 01:00\u201302:00", vm.SelectedBlockText);

        vm.RemoveSelectedBlockCommand.Execute(null);

        Assert.Equal([new WeeklyRangeDto(3, 60, 120)], vm.BuildRule()!.BlockedRanges);
        Assert.False(vm.HasSelectedBlock);
    }

    [Fact]
    public async Task Presets_add_ranges_and_clear_removes_them()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");

        vm.ApplyPresetCommand.Execute("work");
        Assert.Equal(Enumerable.Range(0, 5).Select(d => new WeeklyRangeDto(d, 540, 1020)), vm.BuildRule()!.BlockedRanges);

        vm.ApplyPresetCommand.Execute("evenings");
        Assert.Equal(7 + 5, vm.BuildRule()!.BlockedRanges.Count);

        vm.ApplyPresetCommand.Execute("clear");
        Assert.Empty(vm.BuildRule()!.BlockedRanges);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Pool_modes_map_to_the_budget_dto()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");

        Assert.Equal(NoBudget, vm.BuildRule()!.Budget);

        vm.BudgetModeIndex = (int)AppBudgetMode.Daily;
        vm.DailyHours = 1;
        vm.DailyMinutes = 30;
        Assert.Equal(new AppBudgetDto(AppBudgetMode.Daily, 90, null, 0), vm.BuildRule()!.Budget);

        vm.BudgetModeIndex = (int)AppBudgetMode.Weekly;
        vm.WeeklyHours = 5;
        vm.WeeklyMinutes = 15;
        Assert.Equal(new AppBudgetDto(AppBudgetMode.Weekly, 0, null, 315), vm.BuildRule()!.Budget);

        vm.BudgetModeIndex = (int)AppBudgetMode.PerWeekday;
        for (int day = 0; day < 7; day++)
        {
            vm.WeekdayBudgets[day].Minutes = day * 10;
        }

        AppBudgetDto perDay = vm.BuildRule()!.Budget;
        Assert.Equal(AppBudgetMode.PerWeekday, perDay.Mode);
        Assert.Equal([0, 10, 20, 30, 40, 50, 60], perDay.PerWeekdayMinutes);
    }

    [Fact]
    public async Task Pool_lengths_are_capped_to_a_day_and_a_week()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");

        vm.BudgetModeIndex = (int)AppBudgetMode.Daily;
        vm.DailyHours = 24;
        vm.DailyMinutes = 59;
        Assert.Equal(1440, vm.BuildRule()!.Budget.DailyMinutes);

        vm.BudgetModeIndex = (int)AppBudgetMode.Weekly;
        vm.WeeklyHours = 168;
        vm.WeeklyMinutes = 59;
        Assert.Equal(10080, vm.BuildRule()!.Budget.WeeklyMinutes);
    }

    [Fact]
    public async Task Save_is_enabled_only_while_the_rule_differs_from_the_stored_one()
    {
        AppLimitsViewModel vm = await CreateAsync();
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.SelectedItem = Item(vm, "Spotify");
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.DailyMinutes = 15;
        Assert.True(vm.IsDirty);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.DailyMinutes = 0;
        Assert.False(vm.IsDirty);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Fields_of_other_pool_modes_do_not_make_a_stored_rule_look_edited()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Spotify");

        vm.WeeklyHours = 9;
        vm.WeekdayBudgets[2].Minutes = 5;

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Saving_sends_every_rule_with_the_edited_one_replaced_and_reloads()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Notepad");
        vm.HandleGridAction(Draw(4, 1200, 1320));

        await vm.SaveCommand.ExecuteAsync(null);

        AppRulesDto sent = Assert.Single(_savedRules);
        Assert.Equal(["Discord", "Spotify", "Notepad"], sent.Rules.Select(r => r.App.DisplayName));
        Assert.Equal([new WeeklyRangeDto(4, 1200, 1320)], sent.Rules[2].BlockedRanges);
        Assert.False(vm.IsDirty);
        Assert.Null(vm.ErrorMessage);
        Assert.True(Item(vm, "Notepad").HasRule);
        Assert.Contains(_snackbar.Shown, s => s.Severity == NotificationSeverity.Success);
    }

    [Fact]
    public async Task Saving_an_emptied_rule_removes_it()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Discord");
        vm.ApplyPresetCommand.Execute("clear");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Spotify"], _savedRules.Single().Rules.Select(r => r.App.DisplayName));
        Assert.False(Item(vm, "Discord").HasRule);
    }

    [Fact]
    public async Task A_refused_save_shows_the_service_message_in_the_banner_and_keeps_the_edit()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Discord");
        vm.ApplyPresetCommand.Execute("clear");
        _setResponse = new OperationResponse(false, ["The app is blocked right now; a rule cannot be loosened."]);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The app is blocked right now; a rule cannot be loosened.", vm.ErrorMessage);
        Assert.True(vm.IsDirty);
        Assert.Empty(_snackbar.Shown);
        Assert.True(Item(vm, "Discord").HasRule);

        vm.ApplyPresetCommand.Execute("work");
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Removing_a_rule_asks_first_and_sends_the_remaining_rules()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Spotify");
        Assert.True(vm.RemoveRuleCommand.CanExecute(null));

        await vm.RemoveRuleCommand.ExecuteAsync(null);

        Assert.Single(_dialogs.Confirmations);
        Assert.Equal(["Discord"], _savedRules.Single().Rules.Select(r => r.App.DisplayName));
        Assert.False(Item(vm, "Spotify").HasRule);
    }

    [Fact]
    public async Task Declining_the_removal_keeps_the_rule()
    {
        AppLimitsViewModel vm = await CreateAsync();
        vm.SelectedItem = Item(vm, "Spotify");
        _dialogs.ConfirmResult = false;

        await vm.RemoveRuleCommand.ExecuteAsync(null);

        Assert.Empty(_savedRules);
    }

    [Fact]
    public async Task An_app_without_a_rule_cannot_be_removed()
    {
        AppLimitsViewModel vm = await CreateAsync();

        vm.SelectedItem = Item(vm, "Notepad");

        Assert.False(vm.RemoveRuleCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(AppRuleState.SessionControlled, "SessionControlled", "Session decides")]
    [InlineData(AppRuleState.ScheduledBlock, "ScheduledBlock", "Blocked")]
    [InlineData(AppRuleState.BudgetExhausted, "PoolExhausted", "Time is up")]
    [InlineData(AppRuleState.Allowed, "Allowed", "Allowed now")]
    public void Describe_words_the_state_without_times_when_the_service_gives_none(AppRuleState state, string expected, string text)
    {
        AppRuleDto rule = new(Discord, [new WeeklyRangeDto(0, 0, 60)], NoBudget);
        AppRuleStatusDto status = new(Discord, state, 0, null, null);

        (string Text, LimitState State) result = AppLimitItemViewModel.Describe(rule, status, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);

        Assert.Equal((text, expected), (result.Text, result.State.ToString()));
    }

    [Fact]
    public void Describe_uses_the_local_zone_and_names_later_days()
    {
        DateTimeOffset now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
        AppRuleDto rule = new(Discord, [new WeeklyRangeDto(0, 0, 60)], NoBudget);
        AppRuleStatusDto status = new(Discord, AppRuleState.ScheduledBlock, 0, null, new DateTimeOffset(2026, 11, 23, 22, 0, 0, TimeSpan.Zero));
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");

        Assert.Equal("Blocked until tomorrow 00:00", AppLimitItemViewModel.Describe(rule, status, now, plusTwo).Text);
        Assert.Equal("Blocked until 22:00", AppLimitItemViewModel.Describe(rule, status, now, TimeZoneInfo.Utc).Text);
    }

    [Fact]
    public void Describe_names_the_period_of_the_pool()
    {
        AppRuleDto weekly = new(Spotify, [], new AppBudgetDto(AppBudgetMode.Weekly, 0, null, 300));
        AppRuleStatusDto status = new(Spotify, AppRuleState.Allowed, 0, 110 * 60, null);

        Assert.Equal("1 h 50 min left this week", AppLimitItemViewModel.Describe(weekly, status, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc).Text);
        Assert.Equal("No limits", AppLimitItemViewModel.Describe(null, null, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc).Text);
    }
}
