// The Calendar page, built like the Insights page: four stat tiles and one calendar card. The card has a single header row (Today,
// arrows, period, Day/Week/Month) and either the host-drawn time grid (Day, Week: drag to create, move and resize, click to select,
// a popover that exists only while something is picked) or the month grid (click a day to open it). Nothing else is always visible.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>The Calendar page. Requires "ui.page".</summary>
public sealed class CalendarPage(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IUiPagePlugin
{
    /// <summary>Most entry titles one month cell carries (the host draws the overflow).</summary>
    public const int MaxChips = 3;

    /// <summary>Most tasks without a time that are listed under the grid.</summary>
    public const int MaxUntimedRows = 6;

    /// <inheritdoc/>
    public UiNavigationItem Navigation { get; } = new("Calendar", UiIcon.Calendar);

    /// <inheritdoc/>
    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        CalendarView view = CalendarView.Of(Store);
        return ValueTask.FromResult(Build(view, State, Store.RecoveredFromCorruption));
    }

    /// <inheritdoc/>
    public override ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return Controller.HandlePageAsync(action, cancellationToken);
    }

    /// <summary>Builds the page from a snapshot (shared by tests).</summary>
    public static UiPage Build(CalendarView view, CalendarUiState state, bool recovered)
    {
        List<UiNode> children = [];
        if (recovered)
        {
            children.Add(Ui.Text("Saved calendar data could not be read, so the calendar started empty. The unreadable text was kept in the plugin's storage for a month.", UiTextStyle.Caption, UiTone.Warning));
        }

        if (view.ReadOnly)
        {
            children.Add(Ui.Text(CalendarController.ReadOnlyText, UiTextStyle.Caption, UiTone.Warning));
        }

        children.Add(Tiles(view));
        children.Add(CalendarCard(view, state));
        return new UiPage("Calendar", "Drag on the calendar to plan a focus session or a task. Sessions start by themselves when their time comes.", children);
    }

    private static UiNode Tiles(CalendarView view)
    {
        NextSession? next = view.NextSession();
        UiNode nextTile;
        if (next is null)
        {
            nextTile = Ui.Stat("Next session", "None", "Drag on the calendar", UiTone.Neutral, UiIcon.Clock);
        }
        else if (next.InProgress)
        {
            nextTile = Ui.Stat("Next session", "Now", Truncate($"{next.Occurrence.Title}, until {CalTime.Clock(CalTime.MinuteOf(next.EndUtc, view.Zone), view.Settings.Use24Hour)}", 40), UiTone.Success, UiIcon.Clock);
        }
        else
        {
            nextTile = Ui.Stat("Next session", CalTime.Countdown(next.StartUtc - view.Now), Truncate($"{next.Occurrence.Title}, {StartText(view, next)}", 40), UiTone.Accent, UiIcon.Clock);
        }

        DayProgress today = view.ProgressOf(view.Today);
        string todayCaption = today.Total == 0 ? "Nothing planned" : today.Done == today.Total ? "All done" : $"{today.Total - today.Done} to go";
        UiTone todayTone = today.Total == 0 ? UiTone.Neutral : today.Done == today.Total ? UiTone.Success : UiTone.Accent;
        int weekMinutes = view.PlannedSessionMinutes(view.Today);
        string week = weekMinutes == 0 ? "0 h" : weekMinutes < 60 ? $"{weekMinutes} min" : $"{(weekMinutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} h";
        int streak = view.Streak;
        return Ui.Grid(
            4,
            nextTile,
            Ui.Stat("Today", $"{today.Done} / {today.Total}", todayCaption, todayTone, UiIcon.Checkmark),
            Ui.Stat("This week", week, "Planned focus", UiTone.Neutral, UiIcon.Timer),
            Ui.Stat("Streak", streak.ToString(CultureInfo.InvariantCulture), "Sessions kept", streak > 0 ? UiTone.Success : UiTone.Neutral, UiIcon.Flame));
    }

    private static string StartText(CalendarView view, NextSession next)
    {
        string time = CalTime.Clock(next.Occurrence.StartMinute, view.Settings.Use24Hour);
        return next.Occurrence.Day == view.Today ? $"today {time}" : next.Occurrence.Day == view.Today + 1 ? $"tomorrow {time}" : $"{CalTime.ShortDate(next.Occurrence.Day)} {time}";
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + ".";

    private static UiCard CalendarCard(CalendarView view, CalendarUiState state)
    {
        CalendarMode mode = state.Mode;
        int anchor = state.Anchor ?? view.Today;
        bool sunday = view.Settings.WeekStartsOnSunday;
        string title = mode switch
        {
            CalendarMode.Day => CalTime.DayTitle(anchor),
            CalendarMode.Week => CalTime.WeekTitle(CalTime.WeekStart(anchor, sunday)),
            _ => CalTime.MonthTitle(anchor),
        };
        List<UiNode> body = [Header(mode, title)];
        if (mode == CalendarMode.Month)
        {
            AddMessage(body, state);
            body.Add(MonthGrid(view, anchor, state.Anchor ?? view.Today, CalendarIds.DayPrefix, compact: false, trim: false, title: null, previousId: null, nextId: null, chips: true));
            return new UiCard(body);
        }

        int[] columns = CalendarGrid.Columns(mode, anchor, sunday);
        List<UiTimeGridEvent> blocks = CalendarGrid.Blocks(view, columns, [.. state.Selection], view.ReadOnly);
        PopoverPlan? popover = CalendarPopover.Plan(view, state, columns, blocks);
        if (popover is not { ShowsMessage: true })
        {
            AddMessage(body, state);
        }

        body.Add(CalendarGrid.Build(view, state, columns, blocks, popover));
        UiNode? untimed = Untimed(view, columns, mode);
        if (untimed is not null)
        {
            body.Add(untimed);
        }

        if (popover is null && CalendarPopover.Inline(view, state) is { } panel)
        {
            body.Add(panel);
        }

        return new UiCard(body);
    }

    private static void AddMessage(List<UiNode> body, CalendarUiState state)
    {
        if (state.Message is { } message)
        {
            body.Add(Ui.Text(message.Text, UiTextStyle.Caption, message.IsError ? UiTone.Danger : UiTone.Success));
        }
    }

    // One compact row: Today and the arrows, the period, the view switch (the current view is the filled one).
    private static UiNode Header(CalendarMode mode, string title) => new UiStack(
        [
            Ui.Row(
                Ui.Button(CalendarIds.Today, "Today", UiButtonVariant.Secondary),
                Ui.Button(CalendarIds.Prev, "‹", UiButtonVariant.Subtle),
                Ui.Button(CalendarIds.Next, "›", UiButtonVariant.Subtle)),
            Ui.Text(title, UiTextStyle.Subtitle),
            Ui.Row(
                ModeButton(CalendarMode.Day, "Day", mode),
                ModeButton(CalendarMode.Week, "Week", mode),
                ModeButton(CalendarMode.Month, "Month", mode)),
        ],
        UiOrientation.Horizontal,
        UiGap.Large);

    private static UiButton ModeButton(CalendarMode mode, string label, CalendarMode current) =>
        Ui.Button(CalendarIds.ViewPrefix + label.ToLowerInvariant(), label, mode == current ? UiButtonVariant.Secondary : UiButtonVariant.Subtle);

    // Tasks without a time have no block on the grid; they are listed under it, only while there are any.
    private static UiNode? Untimed(CalendarView view, int[] columns, CalendarMode mode)
    {
        Occurrence[] items = [.. view.Between(columns[0], columns[^1]).Where(o => !o.HasTime)];
        if (items.Length == 0)
        {
            return null;
        }

        List<UiNode> rows = [Ui.Text("No time set", UiTextStyle.Caption)];
        foreach (Occurrence occurrence in items.Take(MaxUntimedRows))
        {
            string label = mode == CalendarMode.Week ? $"{CalTime.ShortDate(occurrence.Day)}  {occurrence.Title}" : occurrence.Title;
            rows.Add(Ui.Row(
                Ui.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, occurrence.Entry.Id, occurrence.OriginalDay), label, occurrence.Done),
                Ui.Button(CalendarIds.Make(CalendarIds.OpenPrefix, occurrence.Entry.Id, occurrence.OriginalDay), "Open", UiButtonVariant.Subtle)));
        }

        if (items.Length > MaxUntimedRows)
        {
            rows.Add(Ui.Text($"+{items.Length - MaxUntimedRows} more", UiTextStyle.Caption));
        }

        return Ui.Column([.. rows]);
    }

    /// <summary>The month as a host-drawn grid: <paramref name="trim"/> drops trailing weeks that hold no day of the month (widgets); <paramref name="chips"/> adds the titles of the day's entries to each cell (full-size grids).</summary>
    public static UiMonthGrid MonthGrid(CalendarView view, int anchor, int selected, string idPrefix, bool compact, bool trim, string? title, string? previousId, string? nextId, bool chips = false)
    {
        bool sunday = view.Settings.WeekStartsOnSunday;
        IReadOnlyList<int> days = CalTime.MonthGrid(anchor, sunday);
        int monthStart = CalTime.MonthStart(anchor);
        if (trim)
        {
            int weeks = 6;
            while (weeks > 4 && days.Skip((weeks - 1) * 7).Take(7).All(d => !CalTime.SameMonth(d, monthStart)))
            {
                weeks--;
            }

            days = [.. days.Take(weeks * 7)];
        }

        ILookup<int, Occurrence> byDay = view.Between(days[0], days[^1]).ToLookup(o => o.Day);
        UiMonthCell[] cells = [.. days.Select(day => DayCell(view, idPrefix, day, byDay[day].ToArray(), day == selected, CalTime.SameMonth(day, monthStart), chips))];
        return Ui.MonthGrid(WeekdayLabels(days[0], compact), cells, title, previousId, nextId, compact);
    }

    private static string[] WeekdayLabels(int firstDay, bool compact) =>
        [.. Enumerable.Range(0, 7).Select(i => compact ? CalTime.DayName(firstDay + i)[..2] : CalTime.DayName(firstDay + i))];

    private static UiMonthCell DayCell(CalendarView view, string idPrefix, int day, Occurrence[] items, bool isSelected, bool inMonth, bool chips)
    {
        int count = items.Length;
        string description = CalTime.LongDate(day) + (count == 0 ? string.Empty : count == 1 ? ", 1 entry" : string.Create(CultureInfo.InvariantCulture, $", {count} entries"));
        return new UiMonthCell(
            idPrefix + day.ToString(CultureInfo.InvariantCulture),
            DateOnly.FromDayNumber(day).Day,
            Math.Min(count, 99),
            IsToday: day == view.Today,
            IsSelected: isSelected,
            IsOutside: !inMonth,
            Tone: UiTone.Accent,
            Description: description,
            Chips: chips && count > 0 ? [.. items.Take(MaxChips).Select(o => o.Title)] : null);
    }
}
