using System.Collections.ObjectModel;
using System.Globalization;
using Blocker.Contracts;
using Blocker.UI.Controls.Services;
using Blocker.UI.Sdk;
using Blocker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Blocker.UI.ViewModels;

/// <summary>One weekday input of the "Each day" time pool.</summary>
internal sealed partial class WeekdayBudgetViewModel(string label) : ObservableObject
{
    [ObservableProperty]
    private double _minutes = AppLimitsViewModel.DefaultDailyMinutes;

    /// <summary>Gets the short weekday name.</summary>
    public string Label { get; } = label;
}

/// <summary>
/// App limits page: pick an app, draw when it is blocked in a week grid, give it a daily or weekly time pool, and save. A running
/// session decides first, then a scheduled block, then the pool; the service enforces that and rejects loosening a rule while its app is blocked.
/// </summary>
internal sealed partial class AppLimitsViewModel : ServiceViewModel
{
    /// <summary>Element id of the week grid.</summary>
    public const string GridId = "limit-grid";

    /// <summary>Pool length offered when an app has none yet, in minutes per day.</summary>
    public const int DefaultDailyMinutes = 60;

    /// <summary>Pool length offered for "Per week" when an app has none yet, in minutes.</summary>
    public const int DefaultWeeklyMinutes = 300;

    private const string BlockPrefix = "block-";
    private const int MaxDailyMinutes = WeeklyBlockSchedule.MinutesPerDay;
    private const int MaxWeeklyMinutes = MaxDailyMinutes * WeeklyBlockSchedule.DaysPerWeek;
    private const int DefaultScrollMinute = 480;

    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly List<AppLimitItemViewModel> _allApps = [];
    private List<AppRuleDto> _rules = [];
    private List<AppRuleStatusDto> _statuses = [];
    private WeeklyBlockSchedule _schedule = new([]);
    private bool _appsLoaded;
    private bool _loadingEditor;
    private bool _rebuildingList;
    private bool _pollFailed;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private AppLimitItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditing), nameof(HasNoEditing), nameof(UsageText), nameof(UsageFraction), nameof(HasUsage))]
    private AppLimitItemViewModel? _editing;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBlockText), nameof(HasSelectedBlock))]
    private WeeklyRangeDto? _selectedRange;

    [ObservableProperty]
    private UiNode? _scheduleNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDailyMode), nameof(IsPerWeekdayMode), nameof(IsWeeklyMode))]
    private int _budgetModeIndex;

    [ObservableProperty]
    private double _dailyHours = 1;

    [ObservableProperty]
    private double _dailyMinutes;

    [ObservableProperty]
    private double _weeklyHours = 5;

    [ObservableProperty]
    private double _weeklyMinutes;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Creates the view model.</summary>
    /// <param name="connection">Service connection.</param>
    /// <param name="snackbar">Snackbars.</param>
    /// <param name="dialogs">Confirmation dialogs.</param>
    /// <param name="time">Time source.</param>
    public AppLimitsViewModel(IServiceConnection connection, ISnackbarService snackbar, IDialogService dialogs, TimeProvider time)
        : base(connection, snackbar)
    {
        _dialogs = dialogs;
        _time = time;
        foreach (string day in DayNames)
        {
            WeekdayBudgets.Add(new WeekdayBudgetViewModel(day));
        }

        foreach (WeekdayBudgetViewModel weekday in WeekdayBudgets)
        {
            weekday.PropertyChanged += (_, _) => OnEditorChanged();
        }

        connection.ConnectionChanged += (_, _) => _ = RefreshAsync(CancellationToken.None);
        BuildGrid();
    }

    /// <summary>Gets the apps that match the search, rule holders first.</summary>
    public ObservableCollection<AppLimitItemViewModel> Items { get; } = [];

    /// <summary>Gets the per-weekday inputs of the "Each day" pool.</summary>
    public ObservableCollection<WeekdayBudgetViewModel> WeekdayBudgets { get; } = [];

    /// <summary>Gets whether an app is open in the editor.</summary>
    public bool HasEditing => Editing is not null;

    /// <summary>Gets whether no app is open in the editor.</summary>
    public bool HasNoEditing => Editing is null;

    /// <summary>Gets whether the list is empty (before the first scan or when nothing matches).</summary>
    public bool HasNoApps => Items.Count == 0;

    /// <summary>Gets whether the "Every day" inputs are shown.</summary>
    public bool IsDailyMode => BudgetModeIndex == (int)AppBudgetMode.Daily;

    /// <summary>Gets whether the "Each day" inputs are shown.</summary>
    public bool IsPerWeekdayMode => BudgetModeIndex == (int)AppBudgetMode.PerWeekday;

    /// <summary>Gets whether the "Per week" inputs are shown.</summary>
    public bool IsWeeklyMode => BudgetModeIndex == (int)AppBudgetMode.Weekly;

    /// <summary>Gets whether a block is selected in the grid.</summary>
    public bool HasSelectedBlock => SelectedRange is not null;

    /// <summary>Gets the day and times of the selected block, or an empty text when none is selected.</summary>
    public string SelectedBlockText => SelectedRange is { } range
        ? $"{DayNames[range.Day]} {FormatMinute(range.StartMinute)}–{FormatMinute(range.EndMinute)}"
        : string.Empty;

    /// <summary>Gets whether the open app has a pool with a live count.</summary>
    public bool HasUsage => Editing is { HasPool: true };

    /// <summary>Gets how much of the current period's pool is used, from 0 to 1.</summary>
    public double UsageFraction => Editing?.UsageFraction ?? 0;

    /// <summary>Gets the usage as two short numbers, for example <c>42/60 min</c> or <c>3.2/5 h</c>.</summary>
    public string UsageText
    {
        get
        {
            if (Editing is not { HasPool: true, Status: { RemainingSeconds: { } remaining } status })
            {
                return string.Empty;
            }

            double used = status.UsedSeconds / 60.0;
            double limit = (status.UsedSeconds + remaining) / 60.0;
            return limit < 180
                ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(used):0}/{Math.Round(limit):0} min")
                : string.Create(CultureInfo.InvariantCulture, $"{used / 60:0.#}/{limit / 60:0.#} h");
        }
    }

    /// <summary>Builds the rule the editor currently describes for the open app.</summary>
    /// <returns>The rule, or <see langword="null"/> when no app is open.</returns>
    public AppRuleDto? BuildRule() =>
        Editing is { } item ? new AppRuleDto(item.Rule?.App ?? item.App.ToEntry(), _schedule.Ranges, BuildBudget()) : null;

    /// <summary>Applies something the user did in the week grid: drawing, moving, resizing or clicking a block.</summary>
    /// <param name="action">The grid's action.</param>
    public void HandleGridAction(UiAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Editing is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case UiActionKind.Schedule when TryReadSlot(action.FormValues, out int day, out int start, out int end):
                if (action.ElementId == GridId)
                {
                    SelectedRange = _schedule.Add(day, start, end);
                }
                else if (TryFindBlock(action.ElementId, out WeeklyRangeDto? old))
                {
                    SelectedRange = _schedule.Replace(old, day, start, end) ?? SelectedRange;
                }

                break;
            case UiActionKind.Click when TryFindBlock(action.ElementId, out WeeklyRangeDto? clicked):
                SelectedRange = clicked;
                break;
            default:
                return;
        }

        BuildGrid();
        OnEditorChanged();
    }

    /// <summary>Reads the status of every rule from the service; used by the page's refresh timer. Reports only the first of a run of failures.</summary>
    /// <returns>A task that completes when the list shows the new status.</returns>
    public async Task PollStatusAsync()
    {
        if (!Connection.IsConnected || !_appsLoaded)
        {
            return;
        }

        try
        {
            AppRulesStatusResponse status = await Connection.GetAppRulesStatusAsync(CancellationToken.None);
            _statuses = [.. status.Items];
            _pollFailed = false;
            ApplyRules();
        }
        catch (Exception ex) when (ServiceFault.IsExpected(ex))
        {
            // Only the first failure is reported: the timer retries every few seconds and would stack snackbars.
            if (!_pollFailed)
            {
                Snackbar.Show("Could not refresh the limits", ServiceFault.Describe(ex), NotificationSeverity.Error);
            }

            _pollFailed = true;
        }
    }

    partial void OnSearchTextChanged(string value) => RebuildList();

    partial void OnSelectedItemChanged(AppLimitItemViewModel? value)
    {
        if (_rebuildingList || value is null || value == Editing)
        {
            return;
        }

        Editing = value;
        ErrorMessage = null;
        LoadEditor(value);
    }

    partial void OnBudgetModeIndexChanged(int value) => OnEditorChanged();

    partial void OnDailyHoursChanged(double value) => OnEditorChanged();

    partial void OnDailyMinutesChanged(double value) => OnEditorChanged();

    partial void OnWeeklyHoursChanged(double value) => OnEditorChanged();

    partial void OnWeeklyMinutesChanged(double value) => OnEditorChanged();

    partial void OnIsDirtyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    partial void OnEditingChanged(AppLimitItemViewModel? value) => RemoveRuleCommand.NotifyCanExecuteChanged();

    /// <summary>Scans installed apps (once) and reads the rules and their status.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task that completes when the list is filled.</returns>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!Connection.IsConnected)
        {
            return;
        }

        ScanAppsResponse? scan = null;
        AppRulesDto? rules = null;
        AppRulesStatusResponse? status = null;
        IsScanning = !_appsLoaded;
        try
        {
            bool done = await TryAsync(
                async () =>
                {
                    if (!_appsLoaded)
                    {
                        scan = await Connection.ScanAppsAsync(cancellationToken);
                    }

                    rules = await Connection.GetAppRulesAsync(cancellationToken);
                    status = await Connection.GetAppRulesStatusAsync(cancellationToken);
                },
                "Could not load the app limits");
            if (!done || rules is null || status is null)
            {
                return;
            }
        }
        finally
        {
            IsScanning = false;
        }

        if (scan is not null)
        {
            _allApps.Clear();
            _allApps.AddRange(scan.Apps.Select(a => new AppLimitItemViewModel(new AppEntryViewModel(a))));
            _appsLoaded = true;
        }

        _rules = [.. rules.Rules];
        _statuses = [.. status.Items];
        ApplyRules();
    }

    [RelayCommand]
    private void RemoveSelectedBlock()
    {
        if (SelectedRange is { } range && _schedule.Remove(range))
        {
            SelectedRange = null;
            BuildGrid();
            OnEditorChanged();
        }
    }

    [RelayCommand]
    private void ApplyPreset(string? preset)
    {
        switch (preset)
        {
            case "work":
                for (int day = 0; day < 5; day++)
                {
                    _ = _schedule.Add(day, 9 * 60, 17 * 60);
                }

                break;
            case "evenings":
                for (int day = 0; day < WeeklyBlockSchedule.DaysPerWeek; day++)
                {
                    _ = _schedule.Add(day, 18 * 60, 23 * 60);
                }

                break;
            case "clear":
                _schedule.Clear();
                break;
            default:
                return;
        }

        SelectedRange = null;
        BuildGrid();
        OnEditorChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Editing is not { } item || BuildRule() is not { } rule)
        {
            return;
        }

        bool hasLimits = rule.BlockedRanges.Count > 0 || rule.Budget.Mode != AppBudgetMode.None;
        List<AppRuleDto> next = [.. _rules.Where(r => !item.App.Matches(r.App))];
        if (hasLimits)
        {
            next.Add(rule);
        }

        await SubmitAsync(next, item, hasLimits ? "Limits saved" : "Limits removed", cancellationToken);
    }

    private bool CanSave() => Editing is not null && IsDirty && Connection.IsConnected;

    [RelayCommand(CanExecute = nameof(CanRemoveRule))]
    private async Task RemoveRuleAsync(CancellationToken cancellationToken)
    {
        if (Editing is not { } item)
        {
            return;
        }

        bool confirmed = await _dialogs.ConfirmAsync(
            string.Create(CultureInfo.InvariantCulture, $"Remove all limits for {item.App.DisplayName}?"),
            "Its scheduled blocks and its time pool are deleted. The service refuses this while the app is blocked.",
            "Remove limits",
            "Keep them",
            cancellationToken);
        if (confirmed)
        {
            await SubmitAsync([.. _rules.Where(r => !item.App.Matches(r.App))], item, "Limits removed", cancellationToken);
        }
    }

    private bool CanRemoveRule() => Editing is { HasRule: true };

    private async Task SubmitAsync(IReadOnlyList<AppRuleDto> rules, AppLimitItemViewModel item, string successTitle, CancellationToken cancellationToken)
    {
        OperationResponse? response = null;
        bool done = await TryAsync(async () => response = await Connection.SetAppRulesAsync(new AppRulesDto(rules), cancellationToken), "Could not save the limits");
        if (!done || response is null)
        {
            return;
        }

        if (!response.Success)
        {
            ErrorMessage = response.Errors.Count > 0 ? string.Join(' ', response.Errors) : "The service refused the change.";
            return;
        }

        ErrorMessage = null;
        await RefreshAsync(cancellationToken);
        if (Editing == item)
        {
            LoadEditor(item);
        }

        Snackbar.Show(successTitle, item.App.DisplayName, NotificationSeverity.Success);
    }

    private void ApplyRules()
    {
        foreach (AppRuleDto rule in _rules.Where(r => !_allApps.Exists(a => a.App.Matches(r.App))))
        {
            _allApps.Add(new AppLimitItemViewModel(new AppEntryViewModel(rule.App)));
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (AppLimitItemViewModel item in _allApps)
        {
            AppRuleDto? rule = _rules.Find(r => item.App.Matches(r.App));
            AppRuleStatusDto? status = rule is null ? null : _statuses.Find(s => item.App.Matches(s.App));
            item.Update(rule, status, now, _time.LocalTimeZone);
        }

        RebuildList();
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(UsageFraction));
        OnPropertyChanged(nameof(HasUsage));
        RemoveRuleCommand.NotifyCanExecuteChanged();
    }

    // The list is only touched when its order or content changes: a status poll every few seconds must not reset the selection or the scroll position.
    private void RebuildList()
    {
        string query = SearchText.Trim();
        List<AppLimitItemViewModel> next =
        [
            .. _allApps
                .Where(a => query.Length == 0 || a.App.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || a.App.Subtitle.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .OrderBy(a => a.HasRule ? 0 : 1)
                .ThenBy(a => a.App.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
        if (Items.SequenceEqual(next))
        {
            return;
        }

        // Clearing the list makes the ListBox write null into SelectedItem; the editor keeps its app and its unsaved changes.
        _rebuildingList = true;
        try
        {
            Items.Clear();
            foreach (AppLimitItemViewModel item in next)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _rebuildingList = false;
        }

        SelectedItem = Editing is not null && Items.Contains(Editing) ? Editing : null;
        OnPropertyChanged(nameof(HasNoApps));
    }

    private void LoadEditor(AppLimitItemViewModel item)
    {
        _loadingEditor = true;
        try
        {
            AppRuleDto? rule = item.Rule;
            _schedule = new WeeklyBlockSchedule(rule?.BlockedRanges ?? []);
            SelectedRange = null;
            AppBudgetDto budget = rule?.Budget ?? NoBudget;
            BudgetModeIndex = (int)budget.Mode;
            SplitMinutes(budget.DailyMinutes > 0 ? budget.DailyMinutes : DefaultDailyMinutes, out double dailyHours, out double dailyMinutes);
            DailyHours = dailyHours;
            DailyMinutes = dailyMinutes;
            SplitMinutes(budget.WeeklyMinutes > 0 ? budget.WeeklyMinutes : DefaultWeeklyMinutes, out double weeklyHours, out double weeklyMinutes);
            WeeklyHours = weeklyHours;
            WeeklyMinutes = weeklyMinutes;
            for (int day = 0; day < WeekdayBudgets.Count; day++)
            {
                WeekdayBudgets[day].Minutes = budget.PerWeekdayMinutes is { Count: WeeklyBlockSchedule.DaysPerWeek } perDay ? perDay[day] : DefaultDailyMinutes;
            }
        }
        finally
        {
            _loadingEditor = false;
        }

        BuildGrid();
        UpdateDirty();
    }

    private void OnEditorChanged()
    {
        if (_loadingEditor)
        {
            return;
        }

        ErrorMessage = null;
        UpdateDirty();
    }

    private void UpdateDirty()
    {
        if (Editing is not { } item)
        {
            IsDirty = false;
            return;
        }

        AppRuleDto? saved = item.Rule;
        bool same = new WeeklyBlockSchedule(saved?.BlockedRanges ?? []).Ranges.SequenceEqual(_schedule.Ranges)
            && BudgetEquals(saved?.Budget ?? NoBudget, BuildBudget());
        IsDirty = !same;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private static AppBudgetDto NoBudget { get; } = new(AppBudgetMode.None, 0, null, 0);

    private AppBudgetDto BuildBudget() => (AppBudgetMode)Math.Clamp(BudgetModeIndex, 0, 3) switch
    {
        AppBudgetMode.Daily => new AppBudgetDto(AppBudgetMode.Daily, JoinMinutes(DailyHours, DailyMinutes, MaxDailyMinutes), null, 0),
        AppBudgetMode.PerWeekday => new AppBudgetDto(AppBudgetMode.PerWeekday, 0, [.. WeekdayBudgets.Select(w => ClampMinutes(w.Minutes, MaxDailyMinutes))], 0),
        AppBudgetMode.Weekly => new AppBudgetDto(AppBudgetMode.Weekly, 0, null, JoinMinutes(WeeklyHours, WeeklyMinutes, MaxWeeklyMinutes)),
        _ => NoBudget,
    };

    // Only the fields of the chosen mode count, so a stored rule with leftovers in the others does not look edited.
    private static bool BudgetEquals(AppBudgetDto a, AppBudgetDto b) =>
        a.Mode == b.Mode
        && a.Mode switch
        {
            AppBudgetMode.Daily => a.DailyMinutes == b.DailyMinutes,
            AppBudgetMode.PerWeekday => (a.PerWeekdayMinutes ?? []).SequenceEqual(b.PerWeekdayMinutes ?? []),
            AppBudgetMode.Weekly => a.WeeklyMinutes == b.WeeklyMinutes,
            _ => true,
        };

    private static int ClampMinutes(double minutes, int max) => (int)Math.Clamp(Math.Round(minutes), 0, max);

    private static int JoinMinutes(double hours, double minutes, int max) => (int)Math.Clamp((Math.Round(hours) * 60) + Math.Round(minutes), 0, max);

    private static void SplitMinutes(int total, out double hours, out double minutes)
    {
        hours = total / 60;
        minutes = total % 60;
    }

    private static string FormatMinute(int minute) => string.Create(CultureInfo.InvariantCulture, $"{minute / 60:00}:{minute % 60:00}");

    private static bool TryReadSlot(IReadOnlyDictionary<string, string> values, out int day, out int start, out int end)
    {
        day = start = end = 0;
        return values.TryGetValue("day", out string? d) && int.TryParse(d, NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
            && values.TryGetValue("start", out string? s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
            && values.TryGetValue("end", out string? e) && int.TryParse(e, NumberStyles.Integer, CultureInfo.InvariantCulture, out end)
            && WeeklyBlockSchedule.IsValid(day, start, end);
    }

    private bool TryFindBlock(string elementId, out WeeklyRangeDto range)
    {
        range = default!;
        if (!elementId.StartsWith(BlockPrefix, StringComparison.Ordinal)
            || !int.TryParse(elementId.AsSpan(BlockPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            || index >= _schedule.Ranges.Count)
        {
            return false;
        }

        range = _schedule.Ranges[index];
        return true;
    }

    private void BuildGrid()
    {
        DateTimeOffset now = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone);
        int today = (((int)now.DayOfWeek) + 6) % 7;
        UiTimeGridDay[] days = [.. DayNames.Select((name, index) => new UiTimeGridDay(string.Create(CultureInfo.InvariantCulture, $"limit-day-{index}"), name, null, index == today))];
        UiTimeGridEvent[] blocks =
        [
            .. _schedule.Ranges.Select((range, index) => new UiTimeGridEvent(
                string.Create(CultureInfo.InvariantCulture, $"{BlockPrefix}{index}"),
                range.Day,
                range.StartMinute,
                range.EndMinute,
                "Blocked",
                $"{FormatMinute(range.StartMinute)}–{FormatMinute(range.EndMinute)}",
                UiTone.Accent,
                range == SelectedRange)),
        ];
        int scrollTo = blocks.Length == 0 ? DefaultScrollMinute : Math.Clamp(blocks.Min(b => b.StartMinute) - 60, 0, 1439);
        ScheduleNode = new UiTimeGrid(GridId, days, blocks, SnapMinutes: 15, ScrollToMinute: scrollTo);
    }
}
