// The Day and Week views: one host-drawn time grid. Occurrences become blocks (a block that crosses midnight is split per day), the
// selection and the popover plan come from the UI state. Pure: a snapshot in, a tree out.

using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>Builds the time grid of the Day and Week views.</summary>
public static class CalendarGrid
{
    /// <summary>Most blocks one grid shows (they count toward the tree's node limit).</summary>
    public const int MaxBlocks = 600;

    /// <summary>Time shown at the top when nothing is planned, in minutes after midnight.</summary>
    public const int DefaultScrollMinute = 8 * 60;

    /// <summary>The days of the visible columns (Day: one, Week: seven) or, for the month, the 42 days of its grid.</summary>
    public static int[] Columns(CalendarMode mode, int anchor, bool sundayFirst) => mode switch
    {
        CalendarMode.Day => [anchor],
        CalendarMode.Week => [.. Enumerable.Range(CalTime.WeekStart(anchor, sundayFirst), 7)],
        _ => [.. CalTime.MonthGrid(anchor, sundayFirst)],
    };

    /// <summary>Whether <paramref name="day"/> is inside the shown period.</summary>
    public static bool IsVisible(CalendarMode mode, int anchor, bool sundayFirst, int day) =>
        Columns(mode, anchor, sundayFirst).Contains(day);

    /// <summary>Whether an occurrence crosses midnight (it is drawn as two blocks and edited with the editor, not by dragging).</summary>
    public static bool Crosses(Occurrence occurrence) => occurrence.Duration > 0 && occurrence.StartMinute + occurrence.Duration > 1440;

    /// <summary>Whether a block can be dragged: timed, in one piece, and not a session that already started, was missed or failed.</summary>
    public static bool IsMovable(CalendarView view, Occurrence occurrence) =>
        occurrence.HasTime && !Crosses(occurrence) && view.OutcomeOf(occurrence) is null;

    /// <summary>The blocks of the occurrences that fall into <paramref name="columns"/>.</summary>
    public static List<UiTimeGridEvent> Blocks(CalendarView view, int[] columns, IReadOnlyCollection<OccurrenceRef> selected, bool readOnly)
    {
        List<UiTimeGridEvent> blocks = [];
        bool h24 = view.Settings.Use24Hour;
        foreach (Occurrence occurrence in view.Between(columns[0] - 1, columns[^1]).Where(o => o.HasTime))
        {
            bool crosses = Crosses(occurrence);
            bool movable = !readOnly && IsMovable(view, occurrence);
            bool isSelected = selected.Contains(new OccurrenceRef(occurrence.Entry.Id, occurrence.OriginalDay));
            int span = occurrence.Duration == 0 ? CalendarForm.PointMinutes : occurrence.Duration;
            string detail = occurrence.Duration == 0
                ? CalTime.Clock(occurrence.StartMinute, h24)
                : $"{CalTime.Clock(occurrence.StartMinute, h24)}–{CalTime.Clock((occurrence.StartMinute + occurrence.Duration) % 1440, h24)}";
            UiTone tone = ToneOf(view, occurrence);
            (int Day, int Start, int End)[] segments = crosses
                ? [(occurrence.Day, occurrence.StartMinute, 1440), (occurrence.Day + 1, 0, occurrence.StartMinute + occurrence.Duration - 1440)]
                : [(occurrence.Day, occurrence.StartMinute, Math.Min(occurrence.StartMinute + span, 1440))];
            for (int i = 0; i < segments.Length; i++)
            {
                int column = Array.IndexOf(columns, segments[i].Day);
                if (column >= 0 && blocks.Count < MaxBlocks)
                {
                    blocks.Add(new UiTimeGridEvent(
                        CalendarIds.Make(CalendarIds.EventPrefix, occurrence.Entry.Id, occurrence.OriginalDay, i),
                        column,
                        segments[i].Start,
                        segments[i].End,
                        occurrence.Title,
                        detail,
                        tone,
                        isSelected,
                        IsMovable: movable,
                        IsResizable: movable));
                }
            }
        }

        return blocks;
    }

    /// <summary>Tone by kind: a session is the accent (success, warning or danger once it started, was missed or failed), a task takes its colour tag and turns green when done.</summary>
    public static UiTone ToneOf(CalendarView view, Occurrence occurrence)
    {
        if (occurrence.Entry.Kind == EntryKind.Task)
        {
            return occurrence.Done ? UiTone.Success : CalendarView.ToneOf(occurrence.Entry.Color);
        }

        return view.OutcomeOf(occurrence) switch
        {
            SessionOutcome.Started => UiTone.Success,
            SessionOutcome.Missed => UiTone.Warning,
            SessionOutcome.Failed => UiTone.Danger,
            _ => UiTone.Accent,
        };
    }

    /// <summary>The time to scroll to: an hour before the first block, or 08:00 when there is none.</summary>
    public static int ScrollMinute(IReadOnlyList<UiTimeGridEvent> blocks) =>
        blocks.Count == 0 ? DefaultScrollMinute : Math.Clamp(blocks.Min(b => b.StartMinute) - 60, 0, 1439);

    /// <summary>The grid for the shown columns, with the popover when there is one.</summary>
    /// <param name="view">Snapshot.</param>
    /// <param name="state">UI state.</param>
    /// <param name="columns">Days of the columns.</param>
    /// <param name="blocks">The blocks (see <see cref="Blocks"/>).</param>
    /// <param name="popover">The popover content and where it is anchored, or <see langword="null"/>.</param>
    public static UiTimeGrid Build(CalendarView view, CalendarUiState state, int[] columns, IReadOnlyList<UiTimeGridEvent> blocks, PopoverPlan? popover)
    {
        bool readOnly = view.ReadOnly;
        UiTimeGridDay[] days =
        [
            .. columns.Select(day => new UiTimeGridDay(
                CalendarIds.ColumnPrefix + day.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CalTime.DayName(day),
                DateOnly.FromDayNumber(day).Day,
                day == view.Today)),
        ];
        int? now = Array.IndexOf(columns, view.Today) >= 0 ? CalTime.MinuteOf(view.Now, view.Zone) : null;
        PendingBlock? pending = state.Pending;
        int pendingColumn = pending is null ? -1 : Array.IndexOf(columns, pending.Day);
        bool ghost = popover?.AnchorId == CalendarIds.Grid && pending is not null && pendingColumn >= 0;
        return new UiTimeGrid(
            CalendarIds.Grid,
            days,
            blocks,
            SnapMinutes: 15,
            NowMinute: now,
            ScrollToMinute: ScrollMinute(blocks),
            AllowCreate: !readOnly,
            Popover: popover?.Content,
            PopoverAnchorId: popover?.AnchorId,
            DismissId: popover is null ? null : CalendarIds.Dismiss,
            PendingDay: ghost ? pendingColumn : null,
            PendingStartMinute: ghost ? pending!.Start : null,
            PendingEndMinute: ghost ? pending!.End : null);
    }
}

/// <summary>What a popover shows and where it hangs.</summary>
/// <param name="Content">The content.</param>
/// <param name="AnchorId">A block id, or the grid id for the pending new block.</param>
/// <param name="ShowsMessage">Whether the content already shows the current message, so the page leaves it out above the grid.</param>
public sealed record PopoverPlan(UiNode Content, string AnchorId, bool ShowsMessage = false);
