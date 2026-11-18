// Focus Insights: records sessions, blocked attempts and emergency passes and shows them as charts.
//
// The base app has no statistics on purpose; this plugin is where they live. Data never leaves the device:
//   * events      SessionStarted/SessionEnded/EmergencyPassStarted (events.session), ProcessBlocked (events.process)
//   * storage     compact per-day aggregates, 90 days of history (see InsightsStore)
//   * UI          a page (ui.page), a small dashboard widget (ui.dashboard) and a settings section (ui.settings)
//   * 50 ms rule  every render combines at most 90 cached day records; storage is never read while rendering

using Blocker.Sdk;
using Blocker.Sdk.Events;
using Blocker.UI.Sdk;

namespace FocusInsights;

/// <summary>Entry point named in plugin.json.</summary>
public sealed class InsightsPlugin : IBlockerPlugin
{
    private InsightsStore? _store;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var store = new InsightsStore(context.Storage, context.Time, context.Logger);
        await store.LoadAsync(cancellationToken);
        _store = store;

        context.Events.Subscribe<SessionStarted>((e, ct) => store.OnSessionStartedAsync(e, ct));
        context.Events.Subscribe<SessionEnded>((e, ct) => store.OnSessionEndedAsync(e, ct));
        context.Events.Subscribe<EmergencyPassStarted>((e, ct) => store.OnEmergencyPassAsync(e, ct));
        context.Events.Subscribe<ProcessBlocked>((e, ct) => store.OnProcessBlockedAsync(e, ct));

        context.Extensions.Add(new InsightsPage(store));
        context.Extensions.Add(new InsightsWidget(store));
        context.Extensions.Add(new InsightsSettings(store));
    }

    /// <inheritdoc/>
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_store is { } store)
        {
            await store.FlushAsync(cancellationToken);
        }
    }
}

/// <summary>The plugin's own logo: three ascending bars breaking out of an open focus ring (0..100 box, even-odd safe).</summary>
public static class InsightsLogo
{
    /// <summary>Path data using only M, A, L, H, V and Z.</summary>
    public const string PathData =
        "M81.95 16.91 A46 46 0 1 1 56.4 4.45 L55.29 12.37 A38 38 0 1 0 76.4 22.67 Z " +
        "M26 56 H38 V74 H26 Z M44 32 H56 V74 H44 Z M62 8 H74 V74 H62 Z";

    /// <summary>Creates the glyph.</summary>
    public static UiGlyph Glyph(double size) => new(PathData, size, UiTone.Accent);
}

/// <summary>Builds the visualizations from an <see cref="InsightsView"/>. Pure: no state, no I/O.</summary>
public static class InsightsLayout
{
    private static readonly string[] Weekdays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] HourLabels = ["0", string.Empty, "4", string.Empty, "8", string.Empty, "12", string.Empty, "16", string.Empty, "20", string.Empty];

    /// <summary>Stat tiles, charts, heatmap and table, in page order.</summary>
    public static List<UiNode> Body(InsightsView view, TimeZoneInfo zone) =>
    [
        Tiles(view),
        FocusChart(view),
        Ui.Grid(2, TopApps(view), Outcomes(view)),
        Heatmap(view),
        RecentSessions(view, zone),
    ];

    private static UiNode Tiles(InsightsView v) => Ui.Grid(
        4,
        Ui.Stat("Focus time", InsightsFormat.Minutes(v.FocusSeconds7d / 60), "Last 7 days", UiTone.Accent, UiIcon.Clock),
        Ui.Stat("Sessions completed", v.Completed7d.ToString(System.Globalization.CultureInfo.InvariantCulture), "Last 7 days", UiTone.Success, UiIcon.Checkmark),
        Ui.Stat("Blocked attempts", v.Blocked7d.ToString(System.Globalization.CultureInfo.InvariantCulture), "Last 7 days", UiTone.Neutral, UiIcon.Shield),
        Ui.Stat(
            "Current streak",
            v.StreakCapped ? $"{v.Streak}+ days" : InsightsFormat.Count(v.Streak, "day"),
            "Days with a completed session",
            v.Streak > 0 ? UiTone.Success : UiTone.Neutral,
            UiIcon.Flame));

    private static UiNode FocusChart(InsightsView v)
    {
        UiDataPoint[] points =
        [
            .. v.DailySeconds14.Select((seconds, i) => Ui.Point(InsightsFormat.DayLabel(v.Today - 13 + i), Math.Round(seconds / 60.0))),
        ];
        var chart = new UiLineChart([new UiSeries("Focus minutes", points)], null, "min");
        return new UiCard([chart], "Focus minutes per day", "Last 14 days");
    }

    private static UiNode TopApps(InsightsView v)
    {
        UiNode body = v.TopApps.Count > 0
            ? new UiBarChart([.. v.TopApps.Select(a => Ui.Point(a.App, a.Count))], null, null)
            : Ui.Text(
                v.Blocked30d > 0
                    ? "No app names were recorded in this period. Recording can be turned on in settings."
                    : "No blocked attempts in this period.",
                UiTextStyle.Caption);
        return new UiCard([body], "Most blocked apps", "Last 30 days");
    }

    private static UiNode Outcomes(InsightsView v)
    {
        int total = v.OutcomeCompleted + v.OutcomeEndedEarly + v.OutcomeOther;
        List<UiNode> body = [];
        if (total > 0)
        {
            List<UiDataPoint> slices = [Ui.Point("Completed", v.OutcomeCompleted), Ui.Point("Ended early", v.OutcomeEndedEarly)];
            if (v.OutcomeOther > 0)
            {
                slices.Add(Ui.Point("Other", v.OutcomeOther));
            }

            body.Add(new UiDonutChart(slices, null, total.ToString(System.Globalization.CultureInfo.InvariantCulture), total == 1 ? "session" : "sessions"));
        }
        else
        {
            body.Add(Ui.Text("No sessions in this period.", UiTextStyle.Caption));
        }

        body.Add(Ui.Text($"Emergency passes used: {v.Passes30d}", UiTextStyle.Caption));
        return new UiCard(body, "Session outcomes", "Last 30 days");
    }

    private static UiNode Heatmap(InsightsView v)
    {
        UiNode body = v.Blocked30d > 0
            ? new UiHeatmap(Weekdays, HourLabels, v.Heatmap, null, "blocked")
            : Ui.Text("No blocked attempts in this period.", UiTextStyle.Caption);
        return new UiCard([body], "When distractions happen", "Blocked attempts by weekday and time of day (2-hour blocks), last 30 days");
    }

    private static UiNode RecentSessions(InsightsView v, TimeZoneInfo zone)
    {
        UiNode body = v.Recent.Count > 0
            ? new UiTable(
                [
                    new UiTableColumn("Started"),
                    new UiTableColumn("Planned", UiAlignment.End, Monospace: true),
                    new UiTableColumn("Actual", UiAlignment.End, Monospace: true),
                    new UiTableColumn("Outcome"),
                ],
                [
                    .. v.Recent.Select(s => new UiTableRow(
                    [
                        InsightsFormat.StartLabel(s.Start, zone),
                        InsightsFormat.Duration(s.Planned),
                        InsightsFormat.Duration(s.Actual),
                        InsightsFormat.OutcomeLabel(s.Reason),
                    ])),
                ])
            : Ui.Text("No sessions recorded yet.", UiTextStyle.Caption);
        return new UiCard([body], "Recent sessions", "Latest first");
    }
}

/// <summary>Remembers the surface of a contribution and re-renders it whenever the recorded data changes.</summary>
public abstract class InsightsContribution : IUiContribution
{
    private IUiSurface? _surface;

    /// <summary>Subscribes to store changes.</summary>
    protected InsightsContribution(InsightsStore store)
    {
        Store = store;
        store.Changed += () => Volatile.Read(ref _surface)?.RequestRender();
    }

    /// <summary>The shared, cached state.</summary>
    protected InsightsStore Store { get; }

    /// <inheritdoc/>
    public virtual ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        return ValueTask.CompletedTask;
    }

    /// <summary>Call at the start of every render and action so a later render can be requested.</summary>
    protected void Remember(UiRenderContext context) => Volatile.Write(ref _surface, context.Surface);
}

/// <summary>The Insights page. Requires "ui.page".</summary>
public sealed class InsightsPage(InsightsStore store) : InsightsContribution(store), IUiPagePlugin
{
    /// <summary>Id of the "Show example" button of the empty state.</summary>
    public const string ExampleButtonId = "insights-example";

    /// <summary>Id of the "Hide example" button of the preview.</summary>
    public const string HideExampleButtonId = "insights-example-hide";

    /// <summary>Caption shown above the example data.</summary>
    public const string ExampleCaption = "Example data — your own stats appear after your first session.";

    private int _showExample;
    private ExampleCache? _example;

    /// <inheritdoc/>
    public UiNavigationItem Navigation { get; } = new("Insights", UiIcon.Chart);

    /// <inheritdoc/>
    public ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        List<UiNode> children = [];
        if (Volatile.Read(ref _showExample) == 1)
        {
            children.Add(Ui.Row(Ui.Text(ExampleCaption, UiTextStyle.Caption), Ui.Button(HideExampleButtonId, "Hide example", UiButtonVariant.Secondary)));
            children.AddRange(InsightsLayout.Body(ExampleView(), Store.LocalTimeZone));
        }
        else
        {
            InsightsView view = Store.GetView();
            if (view.HasData)
            {
                children.AddRange(InsightsLayout.Body(view, Store.LocalTimeZone));
            }
            else
            {
                children.Add(EmptyState());
            }
        }

        return ValueTask.FromResult(new UiPage("Insights", "Your focus sessions and blocked distractions at a glance.", children));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind != UiActionKind.Click)
        {
            return;
        }

        switch (action.ElementId)
        {
            case ExampleButtonId:
                Volatile.Write(ref _showExample, 1);
                context.Surface.RequestRender();
                break;
            case HideExampleButtonId:
                Volatile.Write(ref _showExample, 0);
                context.Surface.RequestRender();
                break;
        }
    }

    private static UiCard EmptyState() => Ui.Card(
        "No data yet",
        Ui.Text("Statistics appear here after your first detox session: focus time, blocked attempts, streaks and when distractions happen."),
        Ui.Text("Everything is recorded on this device only and can be cleared in settings.", UiTextStyle.Caption),
        Ui.Button(ExampleButtonId, "Show example", UiButtonVariant.Secondary));

    // The example depends only on the current day and time zone, so it is built once per day.
    private InsightsView ExampleView()
    {
        int today = Store.CurrentDay;
        TimeZoneInfo zone = Store.LocalTimeZone;
        if (Volatile.Read(ref _example) is { } cached && cached.Day == today && cached.Zone == zone)
        {
            return cached.View;
        }

        var created = new ExampleCache(today, zone, InsightsSampleData.Create(today, zone));
        Volatile.Write(ref _example, created);
        return created.View;
    }

    private sealed record ExampleCache(int Day, TimeZoneInfo Zone, InsightsView View);
}

/// <summary>Small dashboard widget: today's focus and the last seven days. Requires "ui.dashboard".</summary>
public sealed class InsightsWidget(InsightsStore store) : InsightsContribution(store), IDashboardWidgetPlugin
{
    /// <inheritdoc/>
    public UiWidgetSize Size => UiWidgetSize.Small;

    /// <inheritdoc/>
    public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        InsightsView view = Store.GetView();
        List<UiNode> body =
        [
            Ui.Row(
                InsightsLogo.Glyph(28),
                Ui.Stat("Focus today", InsightsFormat.Minutes(view.FocusSecondsToday / 60), "Time in sessions today", UiTone.Accent)),
        ];
        if (view.HasData)
        {
            UiDataPoint[] week =
            [
                .. view.DailySeconds14.Skip(7).Select((seconds, i) => Ui.Point(InsightsFormat.WeekdayLabel(view.Today - 6 + i), seconds / 60)),
            ];
            body.Add(new UiBarChart(week, "Last 7 days", "min"));
        }
        else
        {
            body.Add(Ui.Text("Stats appear after your first session.", UiTextStyle.Caption));
        }

        return ValueTask.FromResult(new UiCard(body, "Focus Insights"));
    }
}

/// <summary>Settings: privacy toggle and clearing the recorded data. Requires "ui.settings".</summary>
public sealed class InsightsSettings(InsightsStore store) : InsightsContribution(store), ISettingsSectionPlugin
{
    /// <summary>Id of the privacy toggle.</summary>
    public const string RecordNamesToggleId = "insights-record-names";

    /// <summary>Id of the clear button.</summary>
    public const string ClearButtonId = "insights-clear";

    /// <summary>How long the clear button waits for its confirming second click.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(10);

    private long _armedAt;
    private int _armed;

    /// <inheritdoc/>
    public string Title => "Focus Insights";

    /// <inheritdoc/>
    public ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
    {
        Remember(context);
        InsightsView view = Store.GetView();
        string stored = view.StoredSessions + view.StoredBlocked == 0
            ? "Nothing is recorded yet."
            : $"Stored on this device: {InsightsFormat.Count(view.StoredSessions, "session")} and {InsightsFormat.Count(view.StoredBlocked, "blocked attempt")} from the last {InsightsLimits.RetentionDays} days.";
        UiButton clear = IsArmed()
            ? Ui.Button(ClearButtonId, "Click again to confirm", UiButtonVariant.Warning, UiIcon.Warning)
            : Ui.Button(ClearButtonId, "Clear recorded data", UiButtonVariant.Secondary, UiIcon.Dismiss);
        return ValueTask.FromResult<UiNode>(Ui.Column(
            Ui.Toggle(
                RecordNamesToggleId,
                "Record blocked app names",
                Store.RecordNames,
                "Lets Insights show which apps were blocked most. When off, attempts are still counted but app names are not stored."),
            Ui.Text(stored, UiTextStyle.Caption),
            clear));
    }

    /// <inheritdoc/>
    public override async ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken)
    {
        await base.OnActionAsync(action, context, cancellationToken);
        if (action.Kind == UiActionKind.Toggle && action.ElementId == RecordNamesToggleId && bool.TryParse(action.Value, out bool on))
        {
            await Store.SetRecordNamesAsync(on, cancellationToken);
        }
        else if (action.Kind == UiActionKind.Click && action.ElementId == ClearButtonId)
        {
            if (IsArmed())
            {
                Volatile.Write(ref _armed, 0);
                await Store.ClearAsync(cancellationToken);   // raises Changed, which requests a render
            }
            else
            {
                Volatile.Write(ref _armedAt, Store.Time.GetTimestamp());
                Volatile.Write(ref _armed, 1);
                context.Surface.RequestRender();
            }
        }
    }

    private bool IsArmed() =>
        Volatile.Read(ref _armed) == 1 && Store.Time.GetElapsedTime(Volatile.Read(ref _armedAt)) < ConfirmWindow;
}
