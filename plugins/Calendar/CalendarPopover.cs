// The floating card of the time grid: it exists only while the user picked something. The quick editor hangs on the pending new block,
// the details of one entry (Edit, Duplicate, Delete) and the multi-selection actions hang on the block clicked last. Pure builders.

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Builds the popover content of the Day and Week views.</summary>
public static class CalendarPopover
{
    /// <summary>The popover for the current state, or <see langword="null"/> when nothing is picked (or the picked entry has no block on the grid).</summary>
    public static PopoverPlan? Plan(CalendarView view, CalendarUiState state, int[] columns, IReadOnlyList<UiTimeGridEvent> blocks)
    {
        if (state.Draft is { EditId: 0 } draft && state.Pending is { } pending && columns.Contains(pending.Day))
        {
            return new PopoverPlan(Editor(view, state, draft), CalendarIds.Grid, ShowsMessage: true);
        }

        Occurrence[] chosen = [.. state.Selection.Select(view.Find).OfType<Occurrence>()];
        if (chosen.Length == 0)
        {
            return null;
        }

        Occurrence last = chosen[^1];
        string? anchor = AnchorOf(state, blocks, last);
        if (anchor is null)
        {
            return null;
        }

        bool editing = state.Draft is { EditId: > 0 } && chosen.Length == 1;
        return new PopoverPlan(Content(view, state, chosen, editing), anchor, ShowsMessage: editing);
    }

    /// <summary>The panel of a selected task without a time, which has no block on the grid; it is drawn under the list of such tasks.</summary>
    public static UiNode? Inline(CalendarView view, CalendarUiState state)
    {
        Occurrence[] chosen = [.. state.Selection.Select(view.Find).OfType<Occurrence>()];
        return chosen is [{ HasTime: false }] ? Content(view, state, chosen, state.Draft is { EditId: > 0 }) : null;
    }

    private static UiNode Content(CalendarView view, CalendarUiState state, Occurrence[] chosen, bool editing) =>
        chosen.Length > 1
            ? Multi(view, state, chosen.Length)
            : editing && state.Draft is { } draft ? Editor(view, state, draft) : Details(view, state, chosen[0]);

    // The block clicked last if it still belongs to the last selected occurrence; otherwise that occurrence's first block.
    private static string? AnchorOf(CalendarUiState state, IReadOnlyList<UiTimeGridEvent> blocks, Occurrence last)
    {
        string prefix = CalendarIds.Make(CalendarIds.EventPrefix, last.Entry.Id, last.OriginalDay) + "-";
        string? clicked = state.AnchorId;
        if (clicked is not null && clicked.StartsWith(prefix, StringComparison.Ordinal) && blocks.Any(b => b.Id == clicked))
        {
            return clicked;
        }

        return blocks.FirstOrDefault(b => b.Id.StartsWith(prefix, StringComparison.Ordinal))?.Id;
    }

    // One button per line: the floating card is narrow and clips a row that is wider than it.
    private static UiNode Multi(CalendarView view, CalendarUiState state, int count)
    {
        List<UiNode> body = [Ui.Text(string.Create(CultureInfo.InvariantCulture, $"{count} selected"), UiTextStyle.Subtitle)];
        if (!view.ReadOnly)
        {
            body.Add(Armed(state, CalendarIds.DeleteSelected, "Delete selected", "Click again to delete"));
            body.Add(Ui.Button(CalendarIds.DuplicateNextWeek, "Duplicate to next week", UiButtonVariant.Subtle));
        }

        body.Add(Ui.Button(CalendarIds.ClearSelection, "Clear selection", UiButtonVariant.Subtle));
        return Ui.Column([.. body]);
    }

    private static UiNode Details(CalendarView view, CalendarUiState state, Occurrence occurrence)
    {
        CalendarEntry entry = occurrence.Entry;
        bool h24 = view.Settings.Use24Hour;
        string when = !occurrence.HasTime
            ? $"{CalTime.LongDate(occurrence.Day)}, no time set"
            : occurrence.Duration == 0
                ? $"{CalTime.LongDate(occurrence.Day)}, {CalTime.Clock(occurrence.StartMinute, h24)}"
                : $"{CalTime.LongDate(occurrence.Day)}, {CalTime.Clock(occurrence.StartMinute, h24)}–{CalTime.Clock((occurrence.StartMinute + occurrence.Duration) % 1440, h24)}";
        List<string> notes = [];
        string detail = view.Detail(occurrence);
        notes.Add(entry.Kind == EntryKind.Task ? (detail.Length == 0 ? "Task" : "Task - " + detail) : detail);
        if (entry.Color != 0)
        {
            notes.Add(CalendarView.ColorName(entry.Color));
        }

        List<UiNode> body =
        [
            Ui.Text(occurrence.Title, UiTextStyle.Subtitle),
            Ui.Text(when, UiTextStyle.Caption),
            Ui.Text(string.Join(" - ", notes), UiTextStyle.Caption, ToneOfOutcome(view.OutcomeOf(occurrence))),
        ];
        if (view.ReadOnly)
        {
            body.Add(Ui.Text(CalendarController.ReadOnlyText, UiTextStyle.Caption, UiTone.Warning));
            return Ui.Column([.. body]);
        }

        // A task without a time is already listed with its own tick box under the grid.
        if (entry.Kind == EntryKind.Task && occurrence.HasTime)
        {
            body.Add(Ui.Toggle(CalendarIds.Make(CalendarIds.DonePrefix, entry.Id, occurrence.OriginalDay), "Done", occurrence.Done));
        }

        bool repeats = entry.Repeat != RepeatKind.Once;
        if (!repeats)
        {
            body.Add(Ui.Row(
                Ui.Button(CalendarIds.Edit, "Edit", UiButtonVariant.Secondary),
                Ui.Button(CalendarIds.Duplicate, "Duplicate", UiButtonVariant.Subtle),
                Armed(state, CalendarIds.Delete, "Delete", "Click again to delete")));
            return Ui.Column([.. body]);
        }

        body.Add(Ui.Row(
            Ui.Button(CalendarIds.Edit, "Edit", UiButtonVariant.Secondary),
            Ui.Button(CalendarIds.Duplicate, "Duplicate", UiButtonVariant.Subtle)));
        body.Add(Armed(state, CalendarIds.Delete, "Delete this one", "Click again to delete"));
        body.Add(Armed(state, CalendarIds.DeleteSeries, "Delete series", "Click again to delete all"));
        return Ui.Column([.. body]);
    }

    private static UiNode Editor(CalendarView view, CalendarUiState state, CalendarDraft draft)
    {
        bool editing = draft.EditId > 0;
        List<UiNode> body = [];
        if (state.Message is { } message)
        {
            body.Add(Ui.Text(message.Text, UiTextStyle.Caption, message.IsError ? UiTone.Danger : UiTone.Success));
        }

        // The date and the switch for the extra fields share the top line, so the form below ends with Save alone.
        body.Add(Ui.Row(
            Ui.Text(CalTime.LongDate(draft.Day), UiTextStyle.Caption),
            Ui.Button(CalendarIds.More, state.ShowMore ? "Fewer options" : "More options", UiButtonVariant.Subtle)));
        // The hint is dropped once the extra fields are open: the card has no room to spare then.
        if (!editing && !state.ShowMore)
        {
            body.Add(Ui.Text(
                view.Settings.AutoStart
                    ? "A focus session starts by itself when its time comes, unless another session is running, and cannot be shortened."
                    : "Auto-start is off in the settings, so planned sessions will not start by themselves.",
                UiTextStyle.Caption));
        }

        List<UiFormField> fields =
        [
            new UiTextField(CalendarForm.TitleId, "Title", draft.Title, draft.Kind == EntryKind.Session ? CalendarForm.DefaultSessionTitle : "What is it?", CalendarLimits.MaxTitleLength),
        ];
        if (!editing)
        {
            fields.Add(new UiSelectField(CalendarForm.KindId, "Kind", [new("session", "Focus session"), new("task", "Task")], draft.Kind == EntryKind.Task ? "task" : "session"));
        }

        fields.Add(new UiTextField(CalendarForm.TimeId, "Start (hh:mm)", draft.Time, "09:00", 8));
        fields.Add(new UiTextField(CalendarForm.ToId, "End (hh:mm)", draft.To, "10:00", 8));
        fields.Add(new UiSelectField(
            CalendarForm.RepeatId,
            "Repeat",
            [new("once", "Does not repeat"), new("daily", "Every day"), new("weekly", "Every week")],
            draft.Repeat switch { RepeatKind.Daily => "daily", RepeatKind.Weekly => "weekly", _ => "once" }));
        if (editing && draft.Repeat != RepeatKind.Once)
        {
            fields.Add(new UiSelectField(CalendarForm.ScopeId, "Apply to", [new("occurrence", "This occurrence only"), new("series", "All occurrences")], draft.EditOccurrenceOnly ? "occurrence" : "series"));
        }

        if (state.ShowMore)
        {
            fields.AddRange(MoreFields(draft));
        }

        body.Add(Ui.Form(CalendarForm.FormId, "Save", [.. fields]));
        return Ui.Column([.. body]);
    }

    // Everything the model has beyond title, time and repeat. The repeat fields are always listed: a form cannot react to its own select.
    private static IEnumerable<UiFormField> MoreFields(CalendarDraft draft)
    {
        yield return new UiTextField(CalendarForm.DateId, "Date (yyyy-mm-dd)", CalTime.IsoDate(draft.Day), "2026-03-09", 10);
        yield return new UiNumberField(CalendarForm.EveryId, "Repeat every N days or weeks", Math.Clamp(draft.Every, 1, CalendarLimits.MaxInterval), 1, CalendarLimits.MaxInterval);
        int mask = draft.Mask != 0 ? draft.Mask : 1 << CalTime.Weekday(draft.Day);
        for (int i = 0; i < 7; i++)
        {
            yield return new UiCheckboxField(CalendarForm.WeekdayPrefix + i.ToString(CultureInfo.InvariantCulture), CalTime.DayNameByIndex(i), (mask & (1 << i)) != 0);
        }

        yield return new UiSelectField(CalendarForm.EndId, "Repeat ends", [new("never", "Never"), new("date", "On the end date"), new("count", "After a number of times")], draft.End switch { EndKind.OnDate => "date", EndKind.AfterCount => "count", _ => "never" });
        yield return new UiTextField(CalendarForm.UntilId, "End date (yyyy-mm-dd)", draft.Until, "2026-06-30", 10);
        yield return new UiNumberField(CalendarForm.CountId, "Number of times", Math.Clamp(draft.Count, 1, CalendarLimits.MaxCount), 1, CalendarLimits.MaxCount);
        yield return new UiSelectField(CalendarForm.ColorId, "Colour tag", [.. Enumerable.Range(0, CalendarLimits.ColorCount).Select(i => new UiOption(i.ToString(CultureInfo.InvariantCulture), CalendarView.ColorName(i)))], draft.Color.ToString(CultureInfo.InvariantCulture));
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
}
