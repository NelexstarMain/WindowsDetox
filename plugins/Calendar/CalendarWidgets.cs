// Dashboard widgets. The host draws the surface, the header (icon and name) and tap-to-open; the trees contain only content.
// The widget API does not tell a widget how many cells it has and starts every widget one column wide, with as many rows (at most 3,
// each about 140 px tall minus the host header) as the content estimate needs, so the content is ordered by importance and the host clips the rest:
//   * CalendarCompactWidget  "Next session": one flat tile, designed for a single 1 x 1 cell (260 x 140 px)
//   * CalendarWidget         "Calendar": next session, a compact month with its own previous/next buttons, the selected day's agenda
//                            with tick boxes, then what is coming; designed for one column and three rows

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>The rich dashboard widget. Requires "ui.dashboard".</summary>
public sealed class CalendarWidget(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IDashboardWidgetPlugin
{
    /// <summary>Rows shown for the selected day.</summary>
    public const int MaxDayRows = 3;

    /// <summary>Entries listed under "Coming up".</summary>
    public const int MaxUpcoming = 2;

    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Medium;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.FromResult(Build(CalendarView.Of(Store), State));
    }

    /// <inheritdoc/>
    public override ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return Controller.HandleWidgetAsync(action, cancellationToken);
    }

    /// <summary>Builds the widget from a snapshot.</summary>
    public static UiCard Build(CalendarView view, CalendarUiState state)
    {
        int today = view.Today;
        int month = state.WidgetMonth ?? today;
        int selected = state.WidgetSelected ?? today;
        List<UiNode> body =
        [
            CalendarCompactWidget.NextStat(view),
            CalendarPage.MonthGrid(view, month, selected, CalendarIds.WidgetDayPrefix, compact: true, trim: true, CalTime.MonthTitle(month), CalendarIds.WidgetPrev, CalendarIds.WidgetNext),
        ];

        Occurrence[] chosen = [.. view.Between(selected, selected)];
        string heading = selected == today ? "Today" : CalTime.ShortDate(selected);
        if (chosen.Length == 0)
        {
            body.Add(Ui.Text($"{heading}: nothing planned.", UiTextStyle.Caption));
        }
        else
        {
            body.Add(Ui.Text(heading, UiTextStyle.Caption));
            body.AddRange(chosen.Take(MaxDayRows).Select(o => Row(view, o)));
            if (chosen.Length > MaxDayRows)
            {
                body.Add(Ui.Text($"+{chosen.Length - MaxDayRows} more", UiTextStyle.Caption));
            }
        }

        Occurrence[] upcoming = [.. view.Between(selected + 1, selected + 60).Take(MaxUpcoming)];
        if (upcoming.Length > 0)
        {
            body.Add(Ui.Text("Coming up", UiTextStyle.Caption));
            body.AddRange(upcoming.Select(o => Ui.Text($"{CalTime.ShortDate(o.Day)}  {view.Line(o)}")));
        }

        return Ui.Card([.. body]);
    }

    private static UiNode Row(CalendarView view, Occurrence occurrence)
    {
        string line = view.Line(occurrence);
        return occurrence.Entry.Kind == EntryKind.Task
            ? Ui.Toggle(CalendarIds.Make(CalendarIds.WidgetDonePrefix, occurrence.Entry.Id, occurrence.OriginalDay), line, occurrence.Done)
            : Ui.Text(view.IsDone(occurrence) ? line + " (started)" : line);
    }
}

/// <summary>The compact dashboard widget "Next session": one flat tile that fits a single cell. Requires "ui.dashboard".</summary>
public sealed class CalendarCompactWidget(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IDashboardWidgetPlugin
{
    private const int CaptionLength = 30;

    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Small;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.FromResult(Build(CalendarView.Of(Store)));
    }

    /// <inheritdoc/>
    public override ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Builds the widget from a snapshot: the tile only, so nothing is clipped in a 1 x 1 cell.</summary>
    public static UiCard Build(CalendarView view) => Ui.Card(NextStat(view));

    /// <summary>The single primary value of both widgets: the next planned session, one short line of caption.</summary>
    public static UiStatTile NextStat(CalendarView view)
    {
        NextSession? next = view.NextSession();
        if (next is null)
        {
            return Ui.Stat("Next session", "None", "Plan one on the Calendar page", UiTone.Neutral);
        }

        if (next.InProgress)
        {
            string until = CalTime.Clock(CalTime.MinuteOf(next.EndUtc, view.Zone), view.Settings.Use24Hour);
            return Ui.Stat("Next session", "Now", Fit(next.Occurrence.Title, $"until {until}"), UiTone.Success);
        }

        string time = CalTime.Clock(next.Occurrence.StartMinute, view.Settings.Use24Hour);
        string when = next.Occurrence.Day == view.Today ? time : next.Occurrence.Day == view.Today + 1 ? "tomorrow " + time : CalTime.ShortDate(next.Occurrence.Day) + " " + time;
        return Ui.Stat("Next session", CalTime.Countdown(next.StartUtc - view.Now), Fit(next.Occurrence.Title, when), UiTone.Accent);
    }

    // "title, suffix" in at most CaptionLength characters: the title gives way, the time stays.
    private static string Fit(string title, string suffix)
    {
        int room = CaptionLength - suffix.Length - 2;
        string shown = room >= title.Length ? title : room >= 2 ? title[..(room - 1)] + "." : string.Empty;
        return shown.Length == 0 ? suffix : string.Create(CultureInfo.InvariantCulture, $"{shown}, {suffix}");
    }
}
