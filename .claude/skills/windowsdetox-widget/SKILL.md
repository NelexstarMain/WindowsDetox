---
name: windowsdetox-widget
description: Design WindowsDetox dashboard widgets (IDashboardWidgetPlugin, capability ui.dashboard). Use when asked to add or restyle a "widget", "widżet", "kafelek", "dashboard tile", "panel na pulpicie", widget size, resize, empty state or what to show on the session dashboard. Defines the host-drawn widget chrome and what the widget tree may contain.
---

# Dashboard widgets (`IDashboardWidgetPlugin`, `ui.dashboard`)

Interface (`src/Blocker.UI.SDK/Contributions.cs`): `UiWidgetSize Size { get; }` and `ValueTask<UiCard> RenderAsync(UiRenderContext, CancellationToken)`; plus `OnActionAsync` from `IUiContribution`. The root must be a `UiCard`; return `Ui.Card(children)` (no title). `UiWidgetSize` is `Small`, `Medium`, `Wide`: the initial size hint only, the user resizes.

## Who draws what (rules)

The host draws: the widget surface, the header with the plugin icon (manifest `icon`, 14 px) and plugin name, tap-to-open the plugin page, and edit mode (drag, resize, remove). Spans (1x1 up to 2x3 cells) are persisted by the host. Therefore the widget tree has:
- NO `UiCard` title/subtitle/icon (`Ui.Card(children)` only), NO `UiGlyph` logo, NO nested cards, NO plugin name text, NO "open page" hint/button.
- ONE primary value: a `UiStatTile` (`Ui.Stat`), rendered flat (no tile box) in widgets. Its label names the value ("Today"), caption is optional.
- A compact month (`UiMonthGrid` with `isCompact`) is acceptable in a calendar widget, right after the primary value; a widget always starts one column wide with up to 3 rows, so keep it to about 5 week rows and put what matters before it or right below it.
- At most ONE `UiProgress` (`Ui.Progress`). At most 3 list rows (toggle rows, text rows or a 3-row `UiTable`). No grids of tiles, no heatmaps, no multi-series charts; a tiny `UiBarChart` is acceptable only in a 2-wide widget and never first.
- A short, calm empty-state line (`Ui.Text(..., UiTextStyle.Caption)`), e.g. "Stats appear after your first session." One line, no button needed (tap opens the page).
- Interactive controls (`UiToggle`, `UiButton`) are allowed but sparing: only for the single quick action users do daily (tick a task, start/stop). Tapping anywhere else opens the plugin page, so never add a button just to navigate. No forms in widgets.

## Size and clipping

- Design for the 1x1 cell (about 260 x 140 px): stat + progress or stat + 1-2 rows must fit; it must be fully understandable there. Wider/taller (2x1, up to 2x3) only adds rows or room, never new meaning.
- Content taller than the cell is clipped with a fade at the bottom: put the most important thing first (stat, then progress, then rows by priority), never a footer that matters.
- Keep strings short (one line each, about 28 chars at 1x1); the validator allows 2000, the layout does not.

## Refresh and cost

- The host polls widgets about every 5 s (`DashboardViewModel.WidgetRefreshInterval`) and re-renders when you call `IUiSurface.RequestRender()`. Each render has the 50 ms callback budget (`BlockerSdk.CallbackBudgetMilliseconds`): read the in-memory store snapshot, no storage reads, no loops over unbounded data, no allocation-heavy formatting. A widget that overruns is left out of its slot, repeated overruns fault the plugin.
- Remember the surface in `RenderAsync` and `OnActionAsync` (`Volatile.Write(ref _surface, context.Surface)`) and call `RequestRender()` when the store changes (pattern: `GoalsContribution`).
- Time-based text (remaining time) comes from `context.Session.Remaining` / `context.Time`, never `DateTime.Now`.
- Element ids in a widget are unique within the widget tree only; still prefix them with the plugin slug (`goals-done-12`), hyphens, no dots.

## Tones

`UiTone.Accent` for the focal value, `Success` when the goal is reached, `Warning`/`Danger` only for real problems. One accent per widget.

## Good example

```csharp
public ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken)
{
    Remember(context);
    GoalsView v = Store.GetView();
    UiTone tone = v.GoalReached ? UiTone.Success : UiTone.Accent;
    List<UiNode> body =
    [
        Ui.Stat("Today", $"{v.Done} / {v.Goal}", v.GoalReached ? "Goal reached" : "tasks to go", tone),
        Ui.Progress(Math.Min(v.Done, v.Goal), v.Goal, null),
    ];
    body.AddRange(v.Tasks.Where(t => !t.Done).Take(3).Select(t => (UiNode)DoneToggle(t))); // max 3 rows
    if (v.Tasks.Count == 0)
    {
        body.Add(Ui.Text("No tasks yet.", UiTextStyle.Caption));
    }

    return ValueTask.FromResult(Ui.Card([.. body]));   // no title, no logo
}
```

## Bad example (what the old widgets did; do not copy)

```csharp
return ValueTask.FromResult(new UiCard(
    [
        Ui.Row(Ui.Glyph(PathData, 28), Ui.Stat("Focus today", "42 min", "Time in sessions today")), // logo + caption noise
        Ui.Card(Ui.BarChart("Last 7 days", points)),                                                // nested card, chart first
        Ui.Grid(2, Ui.Stat("A", "1"), Ui.Stat("B", "2")),                                           // several primary values
        Ui.Button("open-page", "Open Insights"),                                                    // navigation is the host's job
    ],
    "Focus Insights"));                                                                              // title duplicates the host header
```

## Tests

`ctx.RenderAsync(ctx.GetExtension<IDashboardWidgetPlugin>())` returns a validated `UiCard`. Assert: `Title`/`Subtitle`/`Icon` are null, no descendant `UiGlyph` or `UiCard`, exactly one `UiStatTile`, <= 1 `UiProgress`, <= 3 rows, the empty-state text for empty data, and that a toggle action changes the store and calls `RequestRender` (`ctx.RenderRequests`). Walk children of `UiPage`, `UiStack`, `UiGrid`, `UiCard` (see `Descendants` in `plugins/GoalsTasks/GoalsTests.cs`).

## Checklist

- [ ] `Ui.Card(children)` root with no title; no glyph, no nested card.
- [ ] One `Ui.Stat` first, <= 1 progress, <= 3 rows, one-line empty state.
- [ ] Understandable at 1x1; important content first; scales to 2x1..2x3.
- [ ] Controls sparing; ids hyphenated; render from cache within 50 ms.
- [ ] Capability `ui.dashboard` declared; test covers empty and filled states.
