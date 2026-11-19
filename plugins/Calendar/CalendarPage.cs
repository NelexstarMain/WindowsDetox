// The Calendar page, built like the Insights page: four stat tiles, the calendar card (month, week or agenda) and the Plan card.
// The month is drawn by the host (UiMonthGrid): weekday header and days share one grid, so they line up.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>The Calendar page. Requires "ui.page".</summary>
public sealed class CalendarPage(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IUiPagePlugin
{
    /// <summary>Most rows an agenda shows.</summary>
    public const int MaxAgendaRows = 40;

    /// <summary>Days an agenda covers.</summary>
    public const int AgendaDays = 14;

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
            children.Add(Ui.Text("Saved calendar data could not be read, so the calendar started empty. The unreadable text was kept in the plugin's storage.", UiTextStyle.Caption, UiTone.Warning));
        }

        if (state.Message is { } message)
        {
            children.Add(Ui.Row(
                Ui.Text(message.Text, UiTextStyle.Caption, message.IsError ? UiTone.Danger : UiTone.Success),
                Ui.Button(CalendarIds.Dismiss, "Dismiss", UiButtonVariant.Subtle, UiIcon.Dismiss)));
        }

        children.Add(Tiles(view));
        children.Add(CalendarCard(view, state));
        children.Add(PlanCard(view, state));
        return new UiPage("Calendar", "Plan focus sessions and tasks. Sessions start by themselves when their time comes.", children);
    }

    private static UiNode Tiles(CalendarView view)
    {
        NextSession? next = view.NextSession();
        UiNode nextTile;
        if (next is null)
        {
            nextTile = Ui.Stat("Next session", "None", "Plan one below", UiTone.Neutral, UiIcon.Clock);
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
        int selected = state.Selected ?? view.Today;
        List<UiNode> body = [Toolbar(mode)];
        string title;
        string subtitle;
        switch (mode)
        {
            case CalendarMode.Month:
                title = CalTime.MonthTitle(anchor);
                subtitle = "Pick a day to see its plan";
                body.Add(MonthGrid(view, anchor, selected, CalendarIds.DayPrefix, compact: false, trim: false, title: null, previousId: null, nextId: null));
                body.AddRange(DaySection(view, state, selected));
                break;
            case CalendarMode.Week:
                int first = CalTime.WeekStart(anchor, view.Settings.WeekStartsOnSunday);
                title = $"{CalTime.ShortDate(first)} - {CalTime.ShortDate(first + 6)}";
                subtitle = "Seven days, grouped by day";
                body.Add(WeekStrip(view, first, selected));
                body.AddRange(WeekRows(view, state, first));
                break;
            default:
                title = "Next 14 days";
                subtitle = $"{CalTime.ShortDate(anchor)} - {CalTime.ShortDate(anchor + AgendaDays - 1)}";
                body.AddRange(AgendaRows(view, state, anchor));
                break;
        }

        return new UiCard(body, title, subtitle);
    }

    // The view switch (the selected view is filled) and the navigation (bordered, compact) sit in one row but look different.
    private static UiNode Toolbar(CalendarMode mode) => new UiStack(
        [
            Ui.Row(
                ModeButton(CalendarMode.Month, "Month", mode),
                ModeButton(CalendarMode.Week, "Week", mode),
                ModeButton(CalendarMode.Agenda, "Agenda", mode)),
            Ui.Row(
                Ui.Button(CalendarIds.Prev, "‹", UiButtonVariant.Secondary),
                Ui.Button(CalendarIds.Today, "Today", UiButtonVariant.Secondary),
                Ui.Button(CalendarIds.Next, "›", UiButtonVariant.Secondary)),
        ],
        UiOrientation.Horizontal,
        UiGap.Large);

    private static UiButton ModeButton(CalendarMode mode, string label, CalendarMode current) =>
        Ui.Button(CalendarIds.ViewPrefix + label.ToLowerInvariant(), label, mode == current ? UiButtonVariant.Primary : UiButtonVariant.Subtle);

    /// <summary>The month as a host-drawn grid: <paramref name="trim"/> drops trailing weeks that hold no day of the month (widgets).</summary>
    public static UiMonthGrid MonthGrid(CalendarView view, int anchor, int selected, string idPrefix, bool compact, bool trim, string? title, string? previousId, string? nextId)
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

        Dictionary<int, int> counts = view.Between(days[0], days[^1]).GroupBy(o => o.Day).ToDictionary(g => g.Key, g => g.Count());
        UiMonthCell[] cells = [.. days.Select(day => DayCell(view, idPrefix, day, counts.GetValueOrDefault(day), day == selected, CalTime.SameMonth(day, monthStart)))];
        return Ui.MonthGrid(WeekdayLabels(days[0], compact), cells, title, previousId, nextId, compact);
    }

    private static UiMonthGrid WeekStrip(CalendarView view, int first, int selected)
    {
        IReadOnlyList<int> days = [.. Enumerable.Range(first, 7)];
        Dictionary<int, int> counts = view.Between(first, first + 6).GroupBy(o => o.Day).ToDictionary(g => g.Key, g => g.Count());
        return Ui.MonthGrid(
            WeekdayLabels(first, compact: false),
            [.. days.Select(day => DayCell(view, CalendarIds.DayPrefix, day, counts.GetValueOrDefault(day), day == selected, inMonth: true))]);
    }

    private static string[] WeekdayLabels(int firstDay, bool compact) =>
        [.. Enumerable.Range(0, 7).Select(i => compact ? CalTime.DayName(firstDay + i)[..2] : CalTime.DayName(firstDay + i))];

    private static UiMonthCell DayCell(CalendarView view, string idPrefix, int day, int count, bool isSelected, bool inMonth)
    {
        string description = CalTime.LongDate(day) + (count == 0 ? string.Empty : count == 1 ? ", 1 entry" : string.Create(CultureInfo.InvariantCulture, $", {count} entries"));
        return new UiMonthCell(
            idPrefix + day.ToString(CultureInfo.InvariantCulture),
            DateOnly.FromDayNumber(day).Day,
            Math.Min(count, 99),
            IsToday: day == view.Today,
            IsSelected: isSelected,
            IsOutside: !inMonth,
            Description: description);
    }

    private static IEnumerable<UiNode> DaySection(CalendarView view, CalendarUiState state, int day)
    {
        Occurrence[] items = [.. view.Between(day, day)];
        yield return Ui.Row(
            Ui.Text(CalTime.LongDate(day) + (day == view.Today ? " (today)" : string.Empty), UiTextStyle.Subtitle),
            Ui.Text(items.Length == 0 ? "Nothing planned" : items.Length == 1 ? "1 entry" : string.Create(CultureInfo.InvariantCulture, $"{items.Length} entries"), UiTextStyle.Caption));
        if (items.Length == 0)
        {
            bool nothingAtAll = !view.Entries.Any();
            yield return Ui.Text(
                nothingAtAll
                    ? "Your calendar is empty. Plan a focus session and it starts by itself when its time comes."
                    : "A free day. Plan a focus session for it, or pick another day.",
                UiTextStyle.Caption);
            yield return Ui.Button(CalendarIds.EmptyPlan, "Plan a session", nothingAtAll ? UiButtonVariant.Primary : UiButtonVariant.Secondary, UiIcon.Clock);
            yield break;
        }

        foreach (Occurrence occurrence in items)
        {
            yield return EntryRow(view, state, occurrence);
        }
    }

    private static IEnumerable<UiNode> WeekRows(CalendarView view, CalendarUiState state, int first)
    {
        IReadOnlyList<Occurrence> all = view.Between(first, first + 6);
        for (int day = first; day < first + 7; day++)
        {
            int current = day;
            Occurrence[] items = [.. all.Where(o => o.Day == current)];
            yield return DayHeader(view, day, items.Length);
            if (items.Length == 0)
            {
                yield return Ui.Text("Free", UiTextStyle.Caption);
            }

            foreach (UiNode row in items.Select(o => EntryRow(view, state, o)))
            {
                yield return row;
            }
        }
    }

    private static UiNode DayHeader(CalendarView view, int day, int count)
    {
        bool today = day == view.Today;
        UiText title = Ui.Text(CalTime.LongDate(day), UiTextStyle.Subtitle, today ? UiTone.Accent : UiTone.Neutral);
        string note = today ? (count == 0 ? "Today" : "Today, " + EntriesText(count)) : count == 0 ? string.Empty : EntriesText(count);
        return note.Length == 0 ? title : Ui.Row(title, Ui.Text(note, UiTextStyle.Caption));
    }

    private static string EntriesText(int count) => count == 1 ? "1 entry" : string.Create(CultureInfo.InvariantCulture, $"{count} entries");

    private static IEnumerable<UiNode> AgendaRows(CalendarView view, CalendarUiState state, int from)
    {
        Occurrence[] all = [.. view.Between(from, from + AgendaDays - 1)];
        if (all.Length == 0)
        {
            yield return Ui.Text("Nothing planned in these days. Plan a focus session and it shows up here.", UiTextStyle.Caption);
            yield return Ui.Button(CalendarIds.EmptyPlan, "Plan a session", view.Entries.Any() ? UiButtonVariant.Secondary : UiButtonVariant.Primary, UiIcon.Clock);
            yield break;
        }

        int shown = 0;
        int lastDay = -1;
        foreach (Occurrence occurrence in all.Take(MaxAgendaRows))
        {
            if (occurrence.Day != lastDay)
            {
                lastDay = occurrence.Day;
                int count = all.Count(o => o.Day == occurrence.Day);
                yield return DayHeader(view, occurrence.Day, count);
            }

            yield return EntryRow(view, state, occurrence);
            shown++;
        }

        if (all.Length > shown)
        {
            yield return Ui.Text($"+{all.Length - shown} more", UiTextStyle.Caption);
        }
    }

    private static UiNode EntryRow(CalendarView view, CalendarUiState state, Occurrence occurrence)
    {
        CalendarEntry entry = occurrence.Entry;
        string line = view.Line(occurrence);
        string detail = view.Detail(occurrence);
        if (entry.Color != 0)
        {
            detail = (detail.Length == 0 ? string.Empty : detail + " - ") + CalendarView.ColorName(entry.Color);
        }

        UiNode main = entry.Kind == EntryKind.Task
            ? Ui.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, entry.Id, occurrence.OriginalDay), line, occurrence.Done, detail.Length == 0 ? null : detail)
            : Ui.Column(Ui.Text(line), Ui.Text(detail, UiTextStyle.Caption, ToneOfOutcome(view.OutcomeOf(occurrence))));
        List<UiNode> row = [main, Ui.Button(CalendarIds.Make(CalendarIds.EditPrefix, entry.Id, occurrence.OriginalDay, occurrence.Day), "Edit", UiButtonVariant.Subtle)];
        if (entry.Repeat != RepeatKind.Once)
        {
            string skipId = CalendarIds.Make(CalendarIds.SkipPrefix, entry.Id, occurrence.OriginalDay);
            row.Add(Armed(state, skipId, "Skip", "Click again to skip"));
        }

        string deleteId = CalendarIds.Make(CalendarIds.DeletePrefix, entry.Id, occurrence.Day);
        row.Add(Armed(state, deleteId, entry.Repeat == RepeatKind.Once ? "Delete" : "Delete all", entry.Repeat == RepeatKind.Once ? "Click again to delete" : "Click again to delete all"));
        return Ui.Row([.. row]);
    }

    private static UiButton Armed(CalendarUiState state, string id, string label, string armedLabel) =>
        state.IsArmed(id)
            ? Ui.Button(id, armedLabel, UiButtonVariant.Warning, UiIcon.Warning)
            : Ui.Button(id, label, UiButtonVariant.Subtle);

    private static UiTone ToneOfOutcome(SessionOutcome? outcome) => outcome switch
    {
        SessionOutcome.Started => UiTone.Success,
        SessionOutcome.Missed => UiTone.Warning,
        SessionOutcome.Failed => UiTone.Danger,
        _ => UiTone.Neutral,
    };

    // Closed: two buttons that open the form. Open: kind and repeat chips (they change what the form asks for), the form with one Save button, Cancel.
    private static UiCard PlanCard(CalendarView view, CalendarUiState state)
    {
        string autoStart = view.Settings.AutoStart
            ? "A session uses your current allowlist and starts by itself when its time comes, unless another session is running. Once started it cannot be shortened."
            : "Auto-start is off in the settings, so planned sessions will not start by themselves.";
        if (state.Draft is not { } draft)
        {
            return new UiCard(
                [
                    Ui.Text(autoStart, UiTextStyle.Caption),
                    Ui.Row(
                        Ui.Button(CalendarIds.PlanSession, "Plan a session", UiButtonVariant.Primary, UiIcon.Clock),
                        Ui.Button(CalendarIds.PlanTask, "Add a task", UiButtonVariant.Secondary, UiIcon.Checkmark)),
                ],
                "Plan",
                "A focus session or a task");
        }

        bool editing = draft.EditId > 0;
        bool session = draft.Kind == EntryKind.Session;
        List<UiNode> body = [];
        if (session)
        {
            body.Add(Ui.Text(autoStart, UiTextStyle.Caption));
        }

        if (!editing)
        {
            body.Add(Ui.Row(
                Ui.Button(CalendarIds.PlanSession, "Focus session", session ? UiButtonVariant.Secondary : UiButtonVariant.Subtle, UiIcon.Clock),
                Ui.Button(CalendarIds.PlanTask, "Task", session ? UiButtonVariant.Subtle : UiButtonVariant.Secondary, UiIcon.Checkmark)));
        }

        body.Add(Ui.Row(
            Ui.Text("Repeat", UiTextStyle.Caption),
            RepeatChip("once", "Once", draft.Repeat == RepeatKind.Once),
            RepeatChip("daily", "Daily", draft.Repeat == RepeatKind.Daily),
            RepeatChip("weekly", "Weekly", draft.Repeat == RepeatKind.Weekly)));

        List<UiFormField> fields =
        [
            new UiTextField(CalendarForm.TitleId, "Title", draft.Title, session ? "Focus session" : "What is it?", CalendarLimits.MaxTitleLength),
            new UiTextField(CalendarForm.DateId, "Date (yyyy-mm-dd)", CalTime.IsoDate(draft.Day), "2026-03-09", 10),
            new UiTextField(CalendarForm.TimeId, session ? "Start time (hh:mm)" : "Time (hh:mm, optional)", draft.Time, "09:00", 8),
            session
                ? new UiNumberField(CalendarForm.LengthId, "Length (minutes)", Math.Clamp(draft.Length, CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes), CalendarLimits.MinSessionMinutes, CalendarLimits.MaxSessionMinutes, 5)
                : new UiNumberField(CalendarForm.LengthId, "Duration (minutes, optional)", Math.Clamp(draft.Length, 0, CalendarLimits.MaxTaskMinutes), 0, CalendarLimits.MaxTaskMinutes, 5),
        ];
        if (draft.Repeat != RepeatKind.Once)
        {
            fields.Add(new UiNumberField(CalendarForm.EveryId, draft.Repeat == RepeatKind.Daily ? "Every N days" : "Every N weeks", draft.Every, 1, CalendarLimits.MaxInterval));
            if (draft.Repeat == RepeatKind.Weekly)
            {
                int mask = draft.Mask != 0 ? draft.Mask : 1 << CalTime.Weekday(draft.Day);
                for (int i = 0; i < 7; i++)
                {
                    fields.Add(new UiCheckboxField(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture), CalTime.DayNameByIndex(i), (mask & (1 << i)) != 0));
                }
            }

            fields.Add(new UiSelectField(CalendarForm.EndId, "Ends", [new("never", "Never"), new("date", "On the end date"), new("count", "After a number of times")], draft.End switch { EndKind.OnDate => "date", EndKind.AfterCount => "count", _ => "never" }));
            fields.Add(new UiTextField(CalendarForm.UntilId, "End date (yyyy-mm-dd)", draft.Until, "2026-06-30", 10));
            fields.Add(new UiNumberField(CalendarForm.CountId, "Number of times", Math.Clamp(draft.Count, 1, CalendarLimits.MaxCount), 1, CalendarLimits.MaxCount));
        }

        fields.Add(new UiSelectField(CalendarForm.ColorId, "Colour tag", [.. Enumerable.Range(0, CalendarLimits.ColorCount).Select(i => new UiOption(i.ToString(CultureInfo.InvariantCulture), CalendarView.ColorName(i)))], draft.Color.ToString(CultureInfo.InvariantCulture)));
        if (editing && draft.Repeat != RepeatKind.Once)
        {
            fields.Add(new UiSelectField(CalendarForm.ScopeId, "Apply to", [new("occurrence", "This occurrence only"), new("series", "All occurrences")], draft.EditOccurrenceOnly ? "occurrence" : "series"));
        }

        body.Add(Ui.Form(CalendarForm.FormId, "Save", [.. fields]));
        body.Add(Ui.Button(CalendarIds.CancelEdit, "Cancel", UiButtonVariant.Secondary, UiIcon.Dismiss));
        return new UiCard(body, editing ? "Edit entry" : session ? "New session" : "New task", editing ? CalTime.ShortDate(draft.EditShownDay) : CalTime.ShortDate(draft.Day));
    }

    private static UiButton RepeatChip(string key, string label, bool selected) =>
        Ui.Button(CalendarIds.RepeatPrefix + key, label, selected ? UiButtonVariant.Secondary : UiButtonVariant.Subtle);
}
