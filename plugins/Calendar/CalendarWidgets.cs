// Dashboard widgets. The host draws the surface, the header (icon and name) and tap-to-open; the trees contain only content.
// The widget API does not tell a widget how many cells it has, so the content is ordered by importance and the host clips the rest:
//   * CalendarCompactWidget  small: next session, today's progress
//   * CalendarWidget         rich: next session, today's agenda, a mini month with dots, the selected day, what is coming, quick add

using System.Globalization;
using Blocker.UI.Sdk;

namespace CalendarPlanner;

/// <summary>The rich dashboard widget. Requires "ui.dashboard".</summary>
public sealed class CalendarWidget(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IDashboardWidgetPlugin
{
    /// <summary>Rows shown for one day.</summary>
    public const int MaxDayRows = 3;

    /// <summary>Entries listed under "Upcoming".</summary>
    public const int MaxUpcoming = 3;

    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Wide;

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
        List<UiNode> body = [CalendarCompactWidget.NextStat(view)];

        // Today's agenda first: it is the most useful thing after the countdown.
        Occurrence[] todays = [.. view.Between(today, today)];
        if (todays.Length == 0)
        {
            body.Add(Ui.Text("Nothing planned today.", UiTextStyle.Caption));
        }
        else
        {
            body.AddRange(todays.Take(MaxDayRows).Select(o => Row(view, o)));
            if (todays.Length > MaxDayRows)
            {
                body.Add(Ui.Text($"+{todays.Length - MaxDayRows} more today", UiTextStyle.Caption));
            }
        }

        body.AddRange(MiniMonth(view, month, selected));
        if (selected != today)
        {
            body.Add(Ui.Text(CalTime.ShortDate(selected), UiTextStyle.Subtitle));
            Occurrence[] chosen = [.. view.Between(selected, selected)];
            if (chosen.Length == 0)
            {
                body.Add(Ui.Text("Nothing planned.", UiTextStyle.Caption));
            }

            body.AddRange(chosen.Take(MaxDayRows).Select(o => Row(view, o)));
            if (chosen.Length > MaxDayRows)
            {
                body.Add(Ui.Text($"+{chosen.Length - MaxDayRows} more", UiTextStyle.Caption));
            }
        }

        Occurrence[] upcoming = [.. view.Between(today + 1, today + 60).Take(MaxUpcoming)];
        if (upcoming.Length > 0)
        {
            body.Add(Ui.Text("Coming up", UiTextStyle.Subtitle));
            body.AddRange(upcoming.Select(o => Ui.Text($"{CalTime.ShortDate(o.Day)}  {view.Line(o)}", UiTextStyle.Caption)));
        }

        body.Add(Ui.Form(
            CalendarIds.WidgetAddForm,
            "Add",
            new UiTextField(CalendarIds.WidgetAddField, "Quick add a task", string.Empty, $"{(selected == today ? "Today" : CalTime.ShortDate(selected))}: 15:30 Call Anna", CalendarController.QuickAddMaxLength)));
        return Ui.Card([.. body]);
    }

    private static IEnumerable<UiNode> MiniMonth(CalendarView view, int month, int selected)
    {
        yield return Ui.Row(
            Ui.Button(CalendarIds.WidgetPrev, "Previous month", UiButtonVariant.Subtle),
            Ui.Text(CalTime.MonthTitle(month), UiTextStyle.Subtitle),
            Ui.Button(CalendarIds.WidgetNext, "Next month", UiButtonVariant.Subtle));
        IReadOnlyList<int> days = CalTime.MonthGrid(month, view.Settings.WeekStartsOnSunday);
        Dictionary<int, int> counts = view.Between(days[0], days[^1]).GroupBy(o => o.Day).ToDictionary(g => g.Key, g => g.Count());
        int monthStart = CalTime.MonthStart(month);
        yield return Ui.Row([.. Enumerable.Range(0, 7).Select(i => Ui.Text(CalTime.DayName(days[0] + i)[..2], UiTextStyle.Caption))]);
        for (int week = 0; week < 6; week++)
        {
            yield return Ui.Row([.. days.Skip(week * 7).Take(7).Select(day => CalendarPage.DayButton(CalendarIds.WidgetDayPrefix, day, counts.GetValueOrDefault(day), day == selected, day == view.Today, CalTime.SameMonth(day, monthStart), todayIsPrimary: true))]);
        }
    }

    private static UiNode Row(CalendarView view, Occurrence occurrence)
    {
        string line = view.Line(occurrence);
        return occurrence.Entry.Kind == EntryKind.Task
            ? Ui.Toggle(CalendarIds.Make(CalendarIds.WidgetDonePrefix, occurrence.Entry.Id, occurrence.OriginalDay), line, occurrence.Done)
            : Ui.Text(view.IsDone(occurrence) ? line + " (started)" : line);
    }
}

/// <summary>The compact dashboard widget: the next session and today's progress. Requires "ui.dashboard".</summary>
public sealed class CalendarCompactWidget(CalendarStore store, CalendarUiState state, CalendarController controller) : CalendarContribution(store, state, controller), IDashboardWidgetPlugin
{
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

    /// <summary>Builds the widget from a snapshot.</summary>
    public static UiCard Build(CalendarView view)
    {
        List<UiNode> body = [NextStat(view)];
        DayProgress today = view.ProgressOf(view.Today);
        if (today.Total > 0)
        {
            body.Add(Ui.Progress(today.Done, today.Total, null));
            body.Add(Ui.Text($"Today: {today.Done} of {today.Total} done", UiTextStyle.Caption));
        }
        else
        {
            body.Add(Ui.Text("Nothing planned today.", UiTextStyle.Caption));
        }

        return Ui.Card([.. body]);
    }

    /// <summary>The single primary value of both widgets: the next planned session.</summary>
    public static UiStatTile NextStat(CalendarView view)
    {
        NextSession? next = view.NextSession();
        if (next is null)
        {
            return Ui.Stat("Next session", "None", "Plan one on the Calendar page", UiTone.Neutral);
        }

        string title = next.Occurrence.Title.Length <= 28 ? next.Occurrence.Title : next.Occurrence.Title[..27] + ".";
        if (next.InProgress)
        {
            return Ui.Stat("Next session", "Now", title, UiTone.Success);
        }

        string time = CalTime.Clock(next.Occurrence.StartMinute, view.Settings.Use24Hour);
        string when = next.Occurrence.Day == view.Today ? time : next.Occurrence.Day == view.Today + 1 ? "tomorrow " + time : CalTime.ShortDate(next.Occurrence.Day) + " " + time;
        return Ui.Stat("Next session", CalTime.Countdown(next.StartUtc - view.Now), $"{title}, {when}", UiTone.Accent);
    }
}
