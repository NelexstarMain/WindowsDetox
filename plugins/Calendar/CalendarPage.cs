// The Calendar page, built like the Insights page: four stat tiles, the calendar card (month, week or agenda) and the Plan card.

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
            nextTile = Ui.Stat("Next session", "Now", Truncate($"{next.Occurrence.Title}, until {CalTime.Clock((CalTime.MinuteOf(next.EndUtc, view.Zone)), view.Settings.Use24Hour)}", 40), UiTone.Success, UiIcon.Clock);
        }
        else
        {
            nextTile = Ui.Stat("Next session", CalTime.Countdown(next.StartUtc - view.Now), Truncate($"{next.Occurrence.Title}, {StartText(view, next)}", 40), UiTone.Accent, UiIcon.Clock);
        }

        DayProgress today = view.ProgressOf(view.Today);
        string todayCaption = today.Total == 0 ? "Nothing planned" : today.Done == today.Total ? "All done" : $"{today.Total - today.Done} to go";
        int weekMinutes = view.PlannedSessionMinutes(view.Today);
        string week = weekMinutes == 0 ? "0 h" : weekMinutes < 60 ? $"{weekMinutes} min" : $"{(weekMinutes / 60.0).ToString("0.#", CultureInfo.InvariantCulture)} h";
        int streak = view.Streak;
        return Ui.Grid(
            4,
            nextTile,
            Ui.Stat("Today", $"{today.Done} / {today.Total}", todayCaption, today.Total > 0 && today.Done == today.Total ? UiTone.Success : UiTone.Accent, UiIcon.Checkmark),
            Ui.Stat("This week", week, "Planned focus", UiTone.Neutral, UiIcon.Calendar),
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
        List<UiNode> body =
        [
            Ui.Row(
                ModeButton(CalendarMode.Month, "Month", mode),
                ModeButton(CalendarMode.Week, "Week", mode),
                ModeButton(CalendarMode.Agenda, "Agenda", mode),
                Ui.Button(CalendarIds.Prev, "Previous", UiButtonVariant.Subtle),
                Ui.Button(CalendarIds.Today, "Today", UiButtonVariant.Subtle),
                Ui.Button(CalendarIds.Next, "Next", UiButtonVariant.Subtle)),
        ];
        string subtitle;
        switch (mode)
        {
            case CalendarMode.Month:
                subtitle = CalTime.MonthTitle(anchor);
                body.AddRange(MonthGrid(view, anchor, selected));
                body.Add(Ui.Text(CalTime.LongDate(selected), UiTextStyle.Subtitle));
                body.AddRange(DayRows(view, state, selected));
                break;
            case CalendarMode.Week:
                int first = CalTime.WeekStart(anchor, view.Settings.WeekStartsOnSunday);
                subtitle = $"{CalTime.ShortDate(first)} - {CalTime.ShortDate(first + 6)}";
                body.AddRange(WeekRows(view, state, first, selected));
                break;
            default:
                subtitle = $"{CalTime.ShortDate(anchor)} - {CalTime.ShortDate(anchor + AgendaDays - 1)}";
                body.AddRange(AgendaRows(view, state, anchor));
                break;
        }

        if (!view.Entries.Any())
        {
            body.Add(Ui.Text("Nothing planned yet. Use the Plan card below to add a focus session or a task.", UiTextStyle.Caption));
        }

        return new UiCard(body, "Calendar", subtitle);
    }

    private static UiButton ModeButton(CalendarMode mode, string label, CalendarMode current) =>
        Ui.Button(CalendarIds.ViewPrefix + label.ToLowerInvariant(), label, mode == current ? UiButtonVariant.Secondary : UiButtonVariant.Subtle);

    private static IEnumerable<UiNode> MonthGrid(CalendarView view, int anchor, int selected)
    {
        bool sunday = view.Settings.WeekStartsOnSunday;
        IReadOnlyList<int> days = CalTime.MonthGrid(anchor, sunday);
        Dictionary<int, int> counts = view.Between(days[0], days[^1]).GroupBy(o => o.Day).ToDictionary(g => g.Key, g => g.Count());
        int monthStart = CalTime.MonthStart(anchor);
        int startOfWeek = days[0];
        yield return Ui.Row([.. Enumerable.Range(0, 7).Select(i => Ui.Text(CalTime.DayName(startOfWeek + i), UiTextStyle.Caption))]);
        for (int week = 0; week < 6; week++)
        {
            yield return Ui.Row([.. days.Skip(week * 7).Take(7).Select(day => DayButton(CalendarIds.DayPrefix, day, counts.GetValueOrDefault(day), day == selected, day == view.Today, CalTime.SameMonth(day, monthStart)))]);
        }
    }

    /// <summary>The button of one day in a month grid: the day number, a dot per entry (at most three, then a plus), brackets for today.</summary>
    public static UiButton DayButton(string prefix, int day, int count, bool isSelected, bool isToday, bool inMonth, bool todayIsPrimary = false)
    {
        int dom = DateOnly.FromDayNumber(day).Day;
        string dots = count == 0 ? string.Empty : new string('•', Math.Min(count, 3)) + (count > 3 ? "+" : string.Empty);
        string text = (inMonth ? string.Empty : "·") + dom.ToString(CultureInfo.InvariantCulture) + dots;
        string label = isToday ? $"[{text}]" : text;
        UiButtonVariant variant = todayIsPrimary && isToday ? UiButtonVariant.Primary : isSelected ? UiButtonVariant.Secondary : UiButtonVariant.Subtle;
        return Ui.Button(prefix + day.ToString(CultureInfo.InvariantCulture), label, variant);
    }

    private static IEnumerable<UiNode> WeekRows(CalendarView view, CalendarUiState state, int first, int selected)
    {
        IReadOnlyList<Occurrence> all = view.Between(first, first + 6);
        for (int day = first; day < first + 7; day++)
        {
            int current = day;
            yield return Ui.Button(CalendarIds.DayPrefix + day.ToString(CultureInfo.InvariantCulture), CalTime.ShortDate(day) + (day == view.Today ? " (today)" : string.Empty), day == selected ? UiButtonVariant.Secondary : UiButtonVariant.Subtle);
            Occurrence[] items = [.. all.Where(o => o.Day == current)];
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

    private static IEnumerable<UiNode> AgendaRows(CalendarView view, CalendarUiState state, int from)
    {
        Occurrence[] all = [.. view.Between(from, from + AgendaDays - 1)];
        if (all.Length == 0)
        {
            yield return Ui.Text("Nothing planned in these days.", UiTextStyle.Caption);
            yield break;
        }

        int shown = 0;
        int lastDay = -1;
        foreach (Occurrence occurrence in all.Take(MaxAgendaRows))
        {
            if (occurrence.Day != lastDay)
            {
                lastDay = occurrence.Day;
                yield return Ui.Text(CalTime.ShortDate(occurrence.Day) + (occurrence.Day == view.Today ? " (today)" : string.Empty), UiTextStyle.Subtitle);
            }

            yield return EntryRow(view, state, occurrence);
            shown++;
        }

        if (all.Length > shown)
        {
            yield return Ui.Text($"+{all.Length - shown} more", UiTextStyle.Caption);
        }
    }

    private static IEnumerable<UiNode> DayRows(CalendarView view, CalendarUiState state, int day)
    {
        Occurrence[] items = [.. view.Between(day, day)];
        if (items.Length == 0)
        {
            yield return Ui.Text($"Nothing planned on {CalTime.ShortDate(day)}.", UiTextStyle.Caption);
        }

        foreach (Occurrence occurrence in items)
        {
            yield return EntryRow(view, state, occurrence);
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

    private static UiCard PlanCard(CalendarView view, CalendarUiState state)
    {
        CalendarDraft draft = state.Draft ?? CalendarForm.NewDraft(state.Selected ?? view.Today, view.Now, view.Zone, view.Settings);
        bool editing = draft.EditId > 0;
        List<UiFormField> fields =
        [
            new UiSelectField(CalendarForm.KindId, "Kind", [new("session", "Focus session"), new("task", "Task or event")], draft.Kind == EntryKind.Session ? "session" : "task"),
            new UiTextField(CalendarForm.TitleId, "Title", draft.Title, draft.Kind == EntryKind.Session ? "Focus session" : "What is it?", CalendarLimits.MaxTitleLength),
            new UiTextField(CalendarForm.DateId, "Date (yyyy-mm-dd)", CalTime.IsoDate(draft.Day), "2026-03-09", 10),
            new UiTextField(CalendarForm.TimeId, "Start time (hh:mm, tasks may leave it empty)", draft.Time, "09:00", 8),
            new UiNumberField(CalendarForm.LengthId, "Length in minutes (sessions 15 to 720)", Math.Clamp(draft.Length, 0, CalendarLimits.MaxSessionMinutes), 0, CalendarLimits.MaxSessionMinutes, 5),
            new UiSelectField(CalendarForm.RepeatId, "Repeat", [new("once", "Once"), new("daily", "Every N days"), new("weekly", "Weekly on the days below")], RepeatValue(draft.Repeat)),
            new UiNumberField(CalendarForm.EveryId, "Every N days or weeks", draft.Every, 1, CalendarLimits.MaxInterval),
        ];
        int mask = draft.Mask != 0 ? draft.Mask : 1 << CalTime.Weekday(draft.Day);
        for (int i = 0; i < 7; i++)
        {
            fields.Add(new UiCheckboxField(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture), CalTime.DayNameByIndex(i), (mask & (1 << i)) != 0));
        }

        fields.Add(new UiSelectField(CalendarForm.EndId, "Ends", [new("never", "Never"), new("date", "On the date below"), new("count", "After the count below")], draft.End switch { EndKind.OnDate => "date", EndKind.AfterCount => "count", _ => "never" }));
        fields.Add(new UiTextField(CalendarForm.UntilId, "End date (yyyy-mm-dd)", draft.Until, "2026-06-30", 10));
        fields.Add(new UiNumberField(CalendarForm.CountId, "Count", Math.Clamp(draft.Count, 1, CalendarLimits.MaxCount), 1, CalendarLimits.MaxCount));
        fields.Add(new UiSelectField(CalendarForm.ColorId, "Colour tag", [.. Enumerable.Range(0, CalendarLimits.ColorCount).Select(i => new UiOption(i.ToString(CultureInfo.InvariantCulture), CalendarView.ColorName(i)))], draft.Color.ToString(CultureInfo.InvariantCulture)));
        if (editing && draft.Repeat != RepeatKind.Once)
        {
            fields.Add(new UiSelectField(CalendarForm.ScopeId, "Apply to", [new("occurrence", "This occurrence only"), new("series", "All occurrences")], draft.EditOccurrenceOnly ? "occurrence" : "series"));
        }

        List<UiNode> body =
        [
            Ui.Text(
                view.Settings.AutoStart
                    ? "A session uses your current allowlist and starts by itself when its time comes, unless another session is running. Once started it cannot be shortened."
                    : "Auto-start is off in the settings, so planned sessions will not start by themselves.",
                UiTextStyle.Caption),
            Ui.Form(CalendarForm.FormId, editing ? "Save changes" : "Add to calendar", [.. fields]),
        ];
        if (editing)
        {
            body.Add(Ui.Button(CalendarIds.CancelEdit, "Cancel editing", UiButtonVariant.Secondary, UiIcon.Dismiss));
        }

        return new UiCard(body, editing ? "Edit entry" : "Plan", editing ? $"{CalTime.ShortDate(draft.EditShownDay)}" : "Sessions and tasks");
    }

    private static string RepeatValue(RepeatKind repeat) => repeat switch { RepeatKind.Daily => "daily", RepeatKind.Weekly => "weekly", _ => "once" };
}
