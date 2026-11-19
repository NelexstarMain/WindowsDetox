// Turns UI actions (clicks, toggles, form submissions) from the page, the widgets and the settings into changes of the store and the UI
// state. Every id is checked and every number parsed defensively; anything unexpected is ignored.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Ids of the page, widget and settings controls.</summary>
public static class CalendarIds
{
    /// <summary>Prefix of the view buttons; <c>month</c>, <c>week</c> or <c>agenda</c> follows.</summary>
    public const string ViewPrefix = "cal-view-";

    /// <summary>Previous month, week or page.</summary>
    public const string Prev = "cal-prev";

    /// <summary>Next month, week or page.</summary>
    public const string Next = "cal-next";

    /// <summary>Jump to today.</summary>
    public const string Today = "cal-today";

    /// <summary>Prefix of the day buttons; the day number follows.</summary>
    public const string DayPrefix = "cal-day-";

    /// <summary>Prefix of the done toggles: <c>cal-done-{entry}-{seriesDay}</c>.</summary>
    public const string DonePrefix = "cal-done-";

    /// <summary>Prefix of the edit buttons: <c>cal-edit-{entry}-{seriesDay}-{shownDay}</c>.</summary>
    public const string EditPrefix = "cal-edit-";

    /// <summary>Prefix of the delete buttons: <c>cal-del-{entry}-{shownDay}</c> (the day keeps the id unique when a series is listed several times).</summary>
    public const string DeletePrefix = "cal-del-";

    /// <summary>Prefix of the skip buttons: <c>cal-skip-{entry}-{seriesDay}</c>.</summary>
    public const string SkipPrefix = "cal-skip-";

    /// <summary>Dismisses the message.</summary>
    public const string Dismiss = "cal-dismiss";

    /// <summary>Leaves edit mode.</summary>
    public const string CancelEdit = "cal-cancel-edit";

    /// <summary>Widget: previous month.</summary>
    public const string WidgetPrev = "cal-w-prev";

    /// <summary>Widget: next month.</summary>
    public const string WidgetNext = "cal-w-next";

    /// <summary>Prefix of the widget's day buttons.</summary>
    public const string WidgetDayPrefix = "cal-w-day-";

    /// <summary>Prefix of the widget's done toggles.</summary>
    public const string WidgetDonePrefix = "cal-w-done-";

    /// <summary>Id of the widget's quick-add form.</summary>
    public const string WidgetAddForm = "cal-w-add";

    /// <summary>Id of the widget's quick-add text field.</summary>
    public const string WidgetAddField = "cal-w-add-text";

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
    /// <summary>Most entries the quick-add field creates in one go: one.</summary>
    public const int QuickAddMaxLength = 80;

    /// <summary>Handles an action of the page.</summary>
    public async ValueTask HandlePageAsync(UiAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case UiActionKind.Toggle when bool.TryParse(action.Value, out bool done) && CalendarIds.TryParse(action.ElementId, CalendarIds.DonePrefix, 2, out int[] t):
                _ = await store.SetDoneAsync(t[0], t[1], done, cancellationToken);
                break;
            case UiActionKind.Submit when action.ElementId == CalendarForm.FormId:
                await SubmitFormAsync(action.FormValues, cancellationToken);
                break;
            case UiActionKind.Click:
                await ClickAsync(action.ElementId, cancellationToken);
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
            case UiActionKind.Submit when action.ElementId == CalendarIds.WidgetAddForm && action.FormValues.TryGetValue(CalendarIds.WidgetAddField, out string? text):
                await QuickAddAsync(text, state.WidgetSelected ?? today, cancellationToken);
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

    /// <summary>Adds a task from the quick-add field: <c>hh:mm title</c> or just a title, on <paramref name="day"/>.</summary>
    public async ValueTask QuickAddAsync(string? text, int day, CancellationToken cancellationToken)
    {
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value.Length > QuickAddMaxLength * 2)
        {
            return;
        }

        if (CalTime.TryParseTime(value, out _))
        {
            return;   // a time alone is not a task
        }

        int minute = -1;
        int space = value.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0 && CalTime.TryParseTime(value[..space], out int parsed))
        {
            minute = parsed;
            value = value[(space + 1)..].Trim();
        }

        if (value.Length == 0)
        {
            return;
        }

        var entry = new CalendarEntry { Kind = EntryKind.Task, Title = value, StartDay = day, StartMinute = minute };
        if (await store.AddAsync(entry, cancellationToken) is null)
        {
            state.SetMessage(new CalendarMessage($"The calendar is full ({CalendarLimits.MaxEntries} entries). Delete some first.", IsError: true));
        }
    }

    private static bool IsValidDay(int day) =>
        day >= new DateOnly(CalendarLimits.MinYear, 1, 1).DayNumber && day <= new DateOnly(CalendarLimits.MaxYear, 12, 31).DayNumber;

    private async ValueTask ClickAsync(string id, CancellationToken cancellationToken)
    {
        int today = store.Today;
        int anchor = state.Anchor ?? today;
        CalendarMode mode = state.Mode;
        if (id.StartsWith(CalendarIds.ViewPrefix, StringComparison.Ordinal))
        {
            switch (id[CalendarIds.ViewPrefix.Length..])
            {
                case "month":
                    state.SetMode(CalendarMode.Month);
                    break;
                case "week":
                    state.SetMode(CalendarMode.Week);
                    break;
                case "agenda":
                    state.SetMode(CalendarMode.Agenda);
                    break;
            }

            return;
        }

        if (CalendarIds.TryParse(id, CalendarIds.DayPrefix, 1, out int[] d) && IsValidDay(d[0]))
        {
            state.SetSelected(d[0]);
            if (mode == CalendarMode.Month && !CalTime.SameMonth(d[0], anchor))
            {
                state.SetAnchor(d[0]);
            }

            return;
        }

        if (CalendarIds.TryParse(id, CalendarIds.EditPrefix, 3, out int[] e))
        {
            StartEdit(e[0], e[1], e[2]);
            return;
        }

        if (CalendarIds.TryParse(id, CalendarIds.DeletePrefix, 2, out int[] del))
        {
            await ConfirmedAsync(id, async () =>
            {
                CalendarEntry? entry = store.Find(del[0]);
                if (entry is not null && await store.DeleteAsync(del[0], cancellationToken))
                {
                    state.SetMessage(new CalendarMessage($"Deleted \"{entry.Title}\".", IsError: false));
                    if (state.Draft?.EditId == del[0])
                    {
                        state.SetDraft(null);
                    }
                }
            });
            return;
        }

        if (CalendarIds.TryParse(id, CalendarIds.SkipPrefix, 2, out int[] skip))
        {
            await ConfirmedAsync(id, async () =>
            {
                CalendarEntry? entry = store.Find(skip[0]);
                if (entry is not null && await store.SkipAsync(skip[0], skip[1], cancellationToken))
                {
                    state.SetMessage(new CalendarMessage($"Skipped \"{entry.Title}\" on {CalTime.ShortDate(skip[1])}.", IsError: false));
                }
            });
            return;
        }

        switch (id)
        {
            case CalendarIds.Prev:
                Navigate(-1, today, anchor, mode);
                break;
            case CalendarIds.Next:
                Navigate(1, today, anchor, mode);
                break;
            case CalendarIds.Today:
                state.SetAnchor(null);
                state.SetSelected(null);
                break;
            case CalendarIds.Dismiss:
                state.SetMessage(null);
                break;
            case CalendarIds.CancelEdit:
                state.SetDraft(null);
                break;
        }
    }

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

    private void Navigate(int direction, int today, int anchor, CalendarMode mode)
    {
        int next = mode switch
        {
            CalendarMode.Month => CalTime.AddMonths(anchor, direction),
            CalendarMode.Week => anchor + (7 * direction),
            _ => anchor + (14 * direction),
        };
        state.SetAnchor(next);
        if (mode == CalendarMode.Month)
        {
            state.SetSelected(next);
        }
    }

    private void StartEdit(int entryId, int seriesDay, int shownDay)
    {
        CalendarEntry? entry = store.Find(entryId);
        Occurrence? occurrence = entry is null ? null : CalendarRecurrence.ExpandEntry(entry, shownDay, shownDay).FirstOrDefault(o => o.OriginalDay == seriesDay);
        if (occurrence is null)
        {
            state.SetMessage(new CalendarMessage("That entry no longer exists.", IsError: true));
            return;
        }

        state.SetDraft(CalendarForm.EditDraft(occurrence));
        state.SetMessage(null);
    }

    private async ValueTask SubmitFormAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        DateTimeOffset now = store.Time.GetUtcNow();
        TimeZoneInfo zone = store.Zone;
        CalendarSettings settings = store.Settings;
        CalendarDraft editing = state.Draft ?? CalendarForm.NewDraft(state.Selected ?? store.Today, now, zone, settings);
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
                state.SetMessage(new CalendarMessage($"The calendar is full ({CalendarLimits.MaxEntries} entries). Delete some first.", IsError: true));
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

        state.SetSelected(shown);
        state.SetAnchor(shown);
        state.SetDraft(null);
        state.SetMessage(new CalendarMessage(outcome.Notice is null ? text : $"{text} {outcome.Notice}", IsError: false));
    }

    private static CalendarDraft DraftFrom(IReadOnlyDictionary<string, string> values, CalendarDraft editing)
    {
        string Get(string id) => values.TryGetValue(id, out string? v) ? v ?? string.Empty : string.Empty;
        int mask = 0;
        for (int i = 0; i < 7; i++)
        {
            if (Get(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture)) == "true")
            {
                mask |= 1 << i;
            }
        }

        _ = CalTime.TryParseDate(Get(CalendarForm.DateId), out int day);
        _ = double.TryParse(Get(CalendarForm.LengthId).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double length);
        _ = double.TryParse(Get(CalendarForm.EveryId).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double every);
        _ = double.TryParse(Get(CalendarForm.CountId).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double count);
        _ = int.TryParse(Get(CalendarForm.ColorId), NumberStyles.None, CultureInfo.InvariantCulture, out int color);
        return editing with
        {
            Kind = Get(CalendarForm.KindId) == "task" ? EntryKind.Task : EntryKind.Session,
            Title = Get(CalendarForm.TitleId),
            Day = day == 0 ? editing.Day : day,
            Time = Get(CalendarForm.TimeId),
            Length = (int)Math.Clamp(length, 0, CalendarLimits.MaxTaskMinutes),
            Repeat = Get(CalendarForm.RepeatId) switch { "daily" => RepeatKind.Daily, "weekly" => RepeatKind.Weekly, _ => RepeatKind.Once },
            Every = (int)Math.Clamp(every, 1, CalendarLimits.MaxInterval),
            Mask = mask,
            End = Get(CalendarForm.EndId) switch { "date" => EndKind.OnDate, "count" => EndKind.AfterCount, _ => EndKind.Never },
            Until = Get(CalendarForm.UntilId),
            Count = (int)Math.Clamp(count, 1, CalendarLimits.MaxCount),
            Color = Math.Clamp(color, 0, CalendarLimits.ColorCount - 1),
            EditOccurrenceOnly = editing.EditId > 0 && editing.EditOccurrenceOnly && Get(CalendarForm.ScopeId) != "series",
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
