// Turns UI actions (clicks, toggles, form submissions, blocks dragged on the grid) from the page, the widgets and the settings into changes
// of the store and the UI state. Every id is checked and every number parsed defensively; anything unexpected is ignored.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Ids of the page, widget and settings controls.</summary>
public static class CalendarIds
{
    /// <summary>Prefix of the view buttons; <c>day</c>, <c>week</c> or <c>month</c> follows.</summary>
    public const string ViewPrefix = "cal-view-";

    /// <summary>Previous day, week or month.</summary>
    public const string Prev = "cal-prev";

    /// <summary>Next day, week or month.</summary>
    public const string Next = "cal-next";

    /// <summary>Jump to today.</summary>
    public const string Today = "cal-today";

    /// <summary>The time grid; it receives the Schedule action of a block dragged on empty space.</summary>
    public const string Grid = "cal-grid";

    /// <summary>Prefix of the grid column headers: <c>cal-col-{day}</c>.</summary>
    public const string ColumnPrefix = "cal-col-";

    /// <summary>Prefix of the grid blocks: <c>cal-ev-{entry}-{seriesDay}-{segment}</c> (a block that crosses midnight has two segments).</summary>
    public const string EventPrefix = "cal-ev-";

    /// <summary>Prefix of the month cells: <c>cal-day-{day}</c>.</summary>
    public const string DayPrefix = "cal-day-";

    /// <summary>Prefix of the done toggles: <c>cal-done-{entry}-{seriesDay}</c>.</summary>
    public const string DonePrefix = "cal-done-";

    /// <summary>Prefix of the buttons that open a task without a time: <c>cal-open-{entry}-{seriesDay}</c>.</summary>
    public const string OpenPrefix = "cal-open-";

    /// <summary>Clicked when the popover is dismissed.</summary>
    public const string Dismiss = "cal-dismiss";

    /// <summary>Popover: edit the selected entry.</summary>
    public const string Edit = "cal-edit";

    /// <summary>Popover: duplicate the selected entry.</summary>
    public const string Duplicate = "cal-dup";

    /// <summary>Popover: delete the selected occurrence (a one-time entry goes completely).</summary>
    public const string Delete = "cal-delete";

    /// <summary>Popover: delete the whole series of the selected occurrence.</summary>
    public const string DeleteSeries = "cal-delete-series";

    /// <summary>Popover: delete every selected occurrence.</summary>
    public const string DeleteSelected = "cal-delete-sel";

    /// <summary>Popover: copy every selected occurrence to the same time next week.</summary>
    public const string DuplicateNextWeek = "cal-dup-week";

    /// <summary>Popover: clear the selection.</summary>
    public const string ClearSelection = "cal-clear-sel";

    /// <summary>Editor: show or hide the extra fields.</summary>
    public const string More = "cal-more";

    /// <summary>Widget: previous month.</summary>
    public const string WidgetPrev = "cal-w-prev";

    /// <summary>Widget: next month.</summary>
    public const string WidgetNext = "cal-w-next";

    /// <summary>Prefix of the widget's day buttons.</summary>
    public const string WidgetDayPrefix = "cal-w-day-";

    /// <summary>Prefix of the widget's done toggles.</summary>
    public const string WidgetDonePrefix = "cal-w-done-";

    /// <summary>Settings: Sunday as first day of the week.</summary>
    public const string SetWeek = "cal-set-week";

    /// <summary>Settings: 24-hour clock.</summary>
    public const string Set24h = "cal-set-24h";

    /// <summary>Settings: shorter default session.</summary>
    public const string SetLengthDown = "cal-set-len-down";

    /// <summary>Settings: longer default session.</summary>
    public const string SetLengthUp = "cal-set-len-up";

    /// <summary>Settings: shorter reminder lead.</summary>
    public const string SetLeadDown = "cal-set-lead-down";

    /// <summary>Settings: longer reminder lead.</summary>
    public const string SetLeadUp = "cal-set-lead-up";

    /// <summary>Settings: auto-start.</summary>
    public const string SetAuto = "cal-set-auto";

    /// <summary>Settings: clear past entries.</summary>
    public const string SetClear = "cal-set-clear";

    /// <summary>Reads <c>prefix</c> followed by <paramref name="count"/> hyphen-separated non-negative integers.</summary>
    public static bool TryParse(string id, string prefix, int count, out int[] numbers)
    {
        numbers = [];
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = id[prefix.Length..].Split('-');
        if (parts.Length != count)
        {
            return false;
        }

        int[] parsed = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        numbers = parsed;
        return true;
    }

    /// <summary>Formats numbers after a prefix.</summary>
    public static string Make(string prefix, params int[] numbers) =>
        prefix + string.Join('-', numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Applies actions.</summary>
public sealed class CalendarController(CalendarStore store, CalendarUiState state)
{
    /// <summary>Shown when the calendar cannot be changed because its data is newer than this version.</summary>
    public const string ReadOnlyText = "This calendar was saved by a newer version of the plugin, so it is read-only here and nothing is overwritten.";

    /// <summary>Handles an action of the page.</summary>
    public async ValueTask HandlePageAsync(UiAction action, CancellationToken cancellationToken)
    {
        if (state.Message is not null)
        {
            state.SetMessage(null);
        }

        switch (action.Kind)
        {
            case UiActionKind.Toggle when bool.TryParse(action.Value, out bool done) && CalendarIds.TryParse(action.ElementId, CalendarIds.DonePrefix, 2, out int[] t):
                _ = await store.SetDoneAsync(t[0], t[1], done, cancellationToken);
                break;
            case UiActionKind.Submit when action.ElementId == CalendarForm.FormId:
                await SubmitFormAsync(action.FormValues, cancellationToken);
                break;
            case UiActionKind.Schedule:
                await ScheduleAsync(action, cancellationToken);
                break;
            case UiActionKind.Click:
                await ClickAsync(action, cancellationToken);
                break;
        }
    }

    /// <summary>Handles an action of the dashboard widget.</summary>
    public async ValueTask HandleWidgetAsync(UiAction action, CancellationToken cancellationToken)
    {
        int today = store.Today;
        switch (action.Kind)
        {
            case UiActionKind.Toggle when bool.TryParse(action.Value, out bool done) && CalendarIds.TryParse(action.ElementId, CalendarIds.WidgetDonePrefix, 2, out int[] t):
                _ = await store.SetDoneAsync(t[0], t[1], done, cancellationToken);
                break;
            case UiActionKind.Click when action.ElementId == CalendarIds.WidgetPrev:
                state.SetWidgetMonth(CalTime.AddMonths(state.WidgetMonth ?? today, -1));
                break;
            case UiActionKind.Click when action.ElementId == CalendarIds.WidgetNext:
                state.SetWidgetMonth(CalTime.AddMonths(state.WidgetMonth ?? today, 1));
                break;
            case UiActionKind.Click when CalendarIds.TryParse(action.ElementId, CalendarIds.WidgetDayPrefix, 1, out int[] d) && IsValidDay(d[0]):
                state.SetWidgetSelected(d[0]);
                break;
        }
    }

    private static bool IsValidDay(int day) =>
        day >= new DateOnly(CalendarLimits.MinYear, 1, 1).DayNumber && day <= new DateOnly(CalendarLimits.MaxYear, 12, 31).DayNumber;

    private Occurrence? Resolve(OccurrenceRef reference) =>
        store.Find(reference.EntryId) is { } entry ? CalendarRecurrence.OccurrenceOf(entry, reference.OriginalDay) : null;

    private async ValueTask ClickAsync(UiAction action, CancellationToken cancellationToken)
    {
        string id = action.ElementId;
        int today = store.Today;
        int anchor = state.Anchor ?? today;
        CalendarMode mode = state.Mode;
        if (CalendarIds.TryParse(id, CalendarIds.EventPrefix, 3, out int[] ev))
        {
            bool extend = action.FormValues.TryGetValue("extend", out string? flag) && flag == "true";
            SelectBlock(new OccurrenceRef(ev[0], ev[1]), id, extend);
            return;
        }

        if ((CalendarIds.TryParse(id, CalendarIds.ColumnPrefix, 1, out int[] column) || CalendarIds.TryParse(id, CalendarIds.DayPrefix, 1, out column)) && IsValidDay(column[0]))
        {
            state.Show(CalendarMode.Day, column[0]);
            return;
        }

        if (CalendarIds.TryParse(id, CalendarIds.OpenPrefix, 2, out int[] open))
        {
            var reference = new OccurrenceRef(open[0], open[1]);
            if (Resolve(reference) is not null)
            {
                state.Select([reference], anchorId: null);
            }

            return;
        }

        if (id.StartsWith(CalendarIds.ViewPrefix, StringComparison.Ordinal))
        {
            switch (id[CalendarIds.ViewPrefix.Length..])
            {
                case "day":
                    state.Show(CalendarMode.Day, state.Anchor);
                    break;
                case "week":
                    state.Show(CalendarMode.Week, state.Anchor);
                    break;
                case "month":
                    state.Show(CalendarMode.Month, state.Anchor);
                    break;
            }

            return;
        }

        switch (id)
        {
            case CalendarIds.Prev:
                state.Show(mode, Step(mode, anchor, -1));
                break;
            case CalendarIds.Next:
                state.Show(mode, Step(mode, anchor, 1));
                break;
            case CalendarIds.Today:
                state.Show(mode, null);
                break;
            case CalendarIds.Dismiss:
                state.Dismiss();
                break;
            case CalendarIds.ClearSelection:
                state.Dismiss();
                break;
            case CalendarIds.More:
                if (state.Draft is not null)
                {
                    state.SetShowMore(!state.ShowMore);
                }

                break;
            case CalendarIds.Edit:
                StartEdit();
                break;
            case CalendarIds.Duplicate:
                await DuplicateAsync(cancellationToken);
                break;
            case CalendarIds.Delete:
                await ConfirmedAsync(id, () => DeleteOneAsync(wholeSeries: false, cancellationToken));
                break;
            case CalendarIds.DeleteSeries:
                await ConfirmedAsync(id, () => DeleteOneAsync(wholeSeries: true, cancellationToken));
                break;
            case CalendarIds.DeleteSelected:
                await ConfirmedAsync(id, () => DeleteSelectedAsync(cancellationToken));
                break;
            case CalendarIds.DuplicateNextWeek:
                await DuplicateNextWeekAsync(cancellationToken);
                break;
        }
    }

    private static int Step(CalendarMode mode, int anchor, int direction) => mode switch
    {
        CalendarMode.Month => CalTime.AddMonths(anchor, direction),
        CalendarMode.Week => anchor + (7 * direction),
        _ => anchor + direction,
    };

    private async ValueTask ConfirmedAsync(string id, Func<ValueTask> action)
    {
        if (state.TryConfirm(id))
        {
            await action();
        }
        else
        {
            state.Arm(id);
        }
    }

    // A plain click selects one occurrence (a second click on it keeps it); with Ctrl or Shift it joins or leaves the selection.
    private void SelectBlock(OccurrenceRef clicked, string blockId, bool extend)
    {
        if (Resolve(clicked) is null)
        {
            return;
        }

        if (!extend)
        {
            state.Select([clicked], blockId);
            return;
        }

        List<OccurrenceRef> selection = [.. state.Selection];
        if (!selection.Remove(clicked))
        {
            selection.Add(clicked);
        }

        if (selection.Count == 0)
        {
            state.Dismiss();
        }
        else
        {
            state.Select(selection, blockId);
        }
    }

    private void StartEdit()
    {
        if (store.ReadOnly || state.Selection is not [{ } only] || Resolve(only) is not { } occurrence)
        {
            return;
        }

        state.BeginEdit(CalendarForm.EditDraft(occurrence));
    }

    // A timed occurrence becomes a pending block with its editor, so its time can be adjusted before it is saved; a task without a time is copied at once.
    private async ValueTask DuplicateAsync(CancellationToken cancellationToken)
    {
        if (store.ReadOnly || state.Selection is not [{ } only] || Resolve(only) is not { } occurrence)
        {
            return;
        }

        if (!occurrence.HasTime)
        {
            await CopyAsync([occurrence], 0, "Duplicated", cancellationToken);
            return;
        }

        int end = Math.Min(occurrence.StartMinute + Math.Max(occurrence.Duration, CalendarForm.PointMinutes), 1440);
        state.BeginCreate(new PendingBlock(occurrence.Day, occurrence.StartMinute, end), CalendarForm.DuplicateDraft(occurrence));
    }

    private async ValueTask DuplicateNextWeekAsync(CancellationToken cancellationToken)
    {
        if (store.ReadOnly)
        {
            return;
        }

        Occurrence[] chosen = [.. state.Selection.Select(Resolve).OfType<Occurrence>()];
        if (chosen.Length > 0)
        {
            await CopyAsync(chosen, 7, "Duplicated", cancellationToken);
        }
    }

    private async ValueTask CopyAsync(Occurrence[] chosen, int shiftDays, string verb, CancellationToken cancellationToken)
    {
        DateTimeOffset now = store.Time.GetUtcNow();
        List<CalendarEntry> entries = [.. store.Entries];
        int copied = 0;
        foreach (Occurrence occurrence in chosen)
        {
            FormOutcome outcome = CalendarForm.Copy(occurrence, shiftDays, entries, now, store.Zone, store.Settings);
            if (outcome.Add is { } add && await store.AddAsync(add, cancellationToken) is { } stored)
            {
                entries.Add(stored);
                copied++;
            }
        }

        state.Dismiss();
        int skipped = chosen.Length - copied;
        string text = shiftDays == 0 ? $"{verb} \"{chosen[0].Title}\"." : $"{verb} {copied} of {chosen.Length} to next week.";
        state.SetMessage(skipped == 0
            ? new CalendarMessage(text, IsError: false)
            : new CalendarMessage($"{text} {skipped} could not be copied (an overlapping session, a time in the past or a full calendar).", IsError: copied == 0));
    }

    private async ValueTask DeleteOneAsync(bool wholeSeries, CancellationToken cancellationToken)
    {
        if (store.ReadOnly || state.Selection is not [{ } only] || Resolve(only) is not { } occurrence)
        {
            return;
        }

        string title = occurrence.Entry.Title;
        bool removed = wholeSeries
            ? await store.DeleteAsync(only.EntryId, cancellationToken)
            : await store.SkipAsync(only.EntryId, only.OriginalDay, cancellationToken);
        state.Dismiss();
        if (removed)
        {
            state.SetMessage(new CalendarMessage(wholeSeries ? $"Deleted \"{title}\" and all its repeats." : $"Deleted \"{occurrence.Title}\".", IsError: false));
        }
    }

    private async ValueTask DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        if (store.ReadOnly)
        {
            return;
        }

        int removed = 0;
        foreach (OccurrenceRef reference in state.Selection)
        {
            if (await store.SkipAsync(reference.EntryId, reference.OriginalDay, cancellationToken))
            {
                removed++;
            }
        }

        state.Dismiss();
        state.SetMessage(new CalendarMessage($"Deleted {removed} {(removed == 1 ? "entry" : "entries")}.", IsError: false));
    }

    // A block dragged on empty space starts a new entry; a block moved or resized changes that occurrence at once.
    private async ValueTask ScheduleAsync(UiAction action, CancellationToken cancellationToken)
    {
        if (!TryParseSchedule(action.FormValues, out int column, out int start, out int end))
        {
            return;
        }

        if (store.ReadOnly)
        {
            state.SetMessage(new CalendarMessage(ReadOnlyText, IsError: true));
            return;
        }

        int[] days = CalendarGrid.Columns(state.Mode, state.Anchor ?? store.Today, store.Settings.WeekStartsOnSunday);
        if (state.Mode == CalendarMode.Month || column >= days.Length)
        {
            return;
        }

        int day = days[column];
        if (action.ElementId == CalendarIds.Grid)
        {
            state.BeginCreate(new PendingBlock(day, start, end), CalendarForm.NewDraft(day, start, end));
            return;
        }

        if (!CalendarIds.TryParse(action.ElementId, CalendarIds.EventPrefix, 3, out int[] ev) || Resolve(new OccurrenceRef(ev[0], ev[1])) is not { } occurrence)
        {
            return;
        }

        await MoveAsync(occurrence, day, start, end, cancellationToken);
    }

    private async ValueTask MoveAsync(Occurrence occurrence, int day, int start, int end, CancellationToken cancellationToken)
    {
        CalendarView view = CalendarView.Of(store);
        if (!CalendarGrid.IsMovable(view, occurrence))
        {
            state.SetMessage(new CalendarMessage("This block cannot be dragged. Open it and use Edit.", IsError: true));
            return;
        }

        FormOutcome outcome = CalendarForm.Move(occurrence, day, start, end, store.Entries, view.Now, store.Zone, view.Settings);
        if (!outcome.Accepted)
        {
            state.SetMessage(new CalendarMessage(outcome.Error!, IsError: true));
            return;
        }

        bool saved = outcome.Replace is { } replace
            ? await store.ReplaceAsync(replace, cancellationToken)
            : await store.OverrideAsync(outcome.OverrideEntryId, outcome.Override!, cancellationToken);
        state.Dismiss();
        state.SetMessage(saved
            ? outcome.Notice is null ? null : new CalendarMessage(outcome.Notice, IsError: false)
            : new CalendarMessage("That change could not be saved.", IsError: true));
    }

    private static bool TryParseSchedule(IReadOnlyDictionary<string, string> values, out int column, out int start, out int end)
    {
        column = start = end = 0;
        return values.TryGetValue("day", out string? d) && int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out column) && column < 7
            && values.TryGetValue("start", out string? s) && int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            && values.TryGetValue("end", out string? e) && int.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out end)
            && start < end && end <= 1440;
    }

    private async ValueTask SubmitFormAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (state.Draft is not { } editing)
        {
            return;
        }

        DateTimeOffset now = store.Time.GetUtcNow();
        TimeZoneInfo zone = store.Zone;
        CalendarSettings settings = store.Settings;
        if (store.ReadOnly)
        {
            state.SetMessage(new CalendarMessage(ReadOnlyText, IsError: true));
            return;
        }

        FormOutcome outcome = CalendarForm.Submit(values, editing, store.Entries, now, zone, settings);
        if (!outcome.Accepted)
        {
            state.SetDraft(DraftFrom(values, editing));
            state.SetMessage(new CalendarMessage(outcome.Error!, IsError: true));
            return;
        }

        string text;
        int shown;
        if (outcome.Add is { } add)
        {
            CalendarEntry? stored = await store.AddAsync(add, cancellationToken);
            if (stored is null)
            {
                state.SetDraft(DraftFrom(values, editing));
                state.SetMessage(new CalendarMessage(
                    store.Entries.Count >= CalendarLimits.MaxEntries ? $"The calendar is full ({CalendarLimits.MaxEntries} entries). Delete some first." : "That entry could not be saved.",
                    IsError: true));
                return;
            }

            shown = stored.StartDay;
            text = $"Added \"{stored.Title}\" on {CalTime.ShortDate(stored.StartDay)}.";
        }
        else if (outcome.Replace is { } replace)
        {
            _ = await store.ReplaceAsync(replace, cancellationToken);
            shown = replace.StartDay;
            text = $"Saved \"{replace.Title}\".";
        }
        else
        {
            OccurrenceOverride change = outcome.Override!;
            _ = await store.OverrideAsync(outcome.OverrideEntryId, change, cancellationToken);
            shown = change.Day;
            text = $"Saved this occurrence of \"{change.Title}\".";
        }

        state.Dismiss();
        if (!CalendarGrid.IsVisible(state.Mode, state.Anchor ?? store.Today, settings.WeekStartsOnSunday, shown))
        {
            state.Show(state.Mode, shown);
        }

        state.SetMessage(new CalendarMessage(outcome.Notice is null ? text : $"{text} {outcome.Notice}", IsError: false));
    }

    private static CalendarDraft DraftFrom(IReadOnlyDictionary<string, string> values, CalendarDraft editing)
    {
        string Get(string id, string fallback) => values.TryGetValue(id, out string? v) ? v ?? string.Empty : fallback;
        int mask = editing.Mask;
        if (Enumerable.Range(0, 7).Any(i => values.ContainsKey(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture))))
        {
            mask = 0;
            for (int i = 0; i < 7; i++)
            {
                if (Get(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture), string.Empty) == "true")
                {
                    mask |= 1 << i;
                }
            }
        }

        int day = values.ContainsKey(CalendarForm.DateId) && CalTime.TryParseDate(Get(CalendarForm.DateId, string.Empty), out int parsed) ? parsed : editing.Day;
        int every = values.ContainsKey(CalendarForm.EveryId) && double.TryParse(Get(CalendarForm.EveryId, string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double e)
            ? (int)Math.Clamp(e, 1, CalendarLimits.MaxInterval)
            : editing.Every;
        int count = values.ContainsKey(CalendarForm.CountId) && double.TryParse(Get(CalendarForm.CountId, string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
            ? (int)Math.Clamp(c, 1, CalendarLimits.MaxCount)
            : editing.Count;
        int color = values.ContainsKey(CalendarForm.ColorId) && int.TryParse(Get(CalendarForm.ColorId, string.Empty), NumberStyles.None, CultureInfo.InvariantCulture, out int tag)
            ? Math.Clamp(tag, 0, CalendarLimits.ColorCount - 1)
            : editing.Color;
        return editing with
        {
            Kind = !values.ContainsKey(CalendarForm.KindId) ? editing.Kind : Get(CalendarForm.KindId, string.Empty) == "task" ? EntryKind.Task : EntryKind.Session,
            Title = Get(CalendarForm.TitleId, editing.Title),
            Day = day,
            Time = Get(CalendarForm.TimeId, editing.Time),
            To = Get(CalendarForm.ToId, editing.To),
            Repeat = !values.ContainsKey(CalendarForm.RepeatId) ? editing.Repeat : Get(CalendarForm.RepeatId, string.Empty) switch { "daily" => RepeatKind.Daily, "weekly" => RepeatKind.Weekly, _ => RepeatKind.Once },
            Every = every,
            Mask = mask,
            End = !values.ContainsKey(CalendarForm.EndId) ? editing.End : Get(CalendarForm.EndId, string.Empty) switch { "date" => EndKind.OnDate, "count" => EndKind.AfterCount, _ => EndKind.Never },
            Until = Get(CalendarForm.UntilId, editing.Until),
            Count = count,
            Color = color,
            EditOccurrenceOnly = editing.EditId > 0 && (values.ContainsKey(CalendarForm.ScopeId) ? Get(CalendarForm.ScopeId, string.Empty) != "series" : editing.EditOccurrenceOnly),
        };
    }

    /// <summary>Handles an action of the settings section.</summary>
    public async ValueTask HandleSettingsAsync(UiAction action, CancellationToken cancellationToken)
    {
        CalendarSettings s = store.Settings;
        switch (action.Kind)
        {
            case UiActionKind.Toggle when bool.TryParse(action.Value, out bool on):
                switch (action.ElementId)
                {
                    case CalendarIds.SetWeek:
                        s.WeekStartsOnSunday = on;
                        break;
                    case CalendarIds.Set24h:
                        s.Use24Hour = on;
                        break;
                    case CalendarIds.SetAuto:
                        s.AutoStart = on;
                        break;
                    default:
                        return;
                }

                await store.SetSettingsAsync(s, cancellationToken);
                break;
            case UiActionKind.Click:
                switch (action.ElementId)
                {
                    case CalendarIds.SetLengthDown:
                        s.DefaultSessionMinutes -= 15;
                        break;
                    case CalendarIds.SetLengthUp:
                        s.DefaultSessionMinutes += 15;
                        break;
                    case CalendarIds.SetLeadDown:
                        s.ReminderMinutes -= 1;
                        break;
                    case CalendarIds.SetLeadUp:
                        s.ReminderMinutes += 1;
                        break;
                    case CalendarIds.SetClear:
                        await ConfirmedAsync(
                            CalendarIds.SetClear,
                            async () =>
                            {
                                int removed = await store.ClearPastAsync(store.Today, cancellationToken);
                                state.SetMessage(new CalendarMessage(removed == 0 ? "No past entries to clear." : $"Cleared {removed} past {(removed == 1 ? "entry" : "entries")}.", IsError: false));
                            });
                        return;
                    default:
                        return;
                }

                await store.SetSettingsAsync(s, cancellationToken);
                break;
        }
    }
}
