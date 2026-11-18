---
name: windowsdetox-plugin-page
description: Design WindowsDetox plugin pages (IUiPagePlugin, ui.page) and settings sections (ISettingsSectionPlugin, ui.settings): layout, cards, charts, tables, forms, empty and example states, confirm-before-destructive buttons. Use when asked to build or restyle a plugin "page", "strona wtyczki", "ekran", "sekcja ustawień", "settings", "wykres", "chart", "formularz", "form", "tabela", "stan pusty", "przykład danych".
---

# Plugin pages and settings sections

Blocks (`UiNodes`), factories (`Ui`) and limits (`UiTreeValidator`) are in the Blocker UI SDK; `PluginMain.cs` in this folder shows a minimal page. Patterns below (charts with an example state, forms, toggles, confirm buttons) come from the bundled Focus Insights and Goals plugins.

## Page (`IUiPagePlugin`, `ui.page`)

- `UiNavigationItem Navigation { get; }` = `new("Goals", UiIcon.Target)`: title 1-32 printable chars, defined `UiIcon` (sidebar shows the manifest `icon` instead when present; the title is still the label and must be unique among sidebar items).
- `RenderAsync` returns `Ui.Page(title, subtitle, children...)` (`UiPage`, only valid as root). The HOST renders the official page title and subtitle as the page header: never repeat the title, the plugin name, a logo (`UiGlyph`) or a "welcome" heading in the content. Title = plugin name or the noun it manages; subtitle = one calm sentence saying what the page is for.
- Content = a vertical sequence of `UiCard`s; a page should be scannable in five seconds. Order: headline numbers, main visual, details, history.
- Card: `new UiCard(children, title, subtitle, icon)` or `Ui.Card(title, children...)`. Title = noun phrase ("Today's tasks"), subtitle = scope ("Last 7 days"). Never nest cards. Wrap a lone visual in a card with a title instead of leaving it bare.
- Stat tiles: `Ui.Stat(label, value, caption, tone, icon)` inside `Ui.Grid(2..4, ...)` (Insights uses 4); value is pre-formatted and short ("1 h 05 min"). Grid columns 1-6.
- Prefer `Ui.Row`/`Ui.Column` (`UiGap` Small/Medium/Large) for grouping; max depth 16.

## Charts and tables (all values finite; texts 1-2000 chars)

| Block | Use | Validator limits |
|---|---|---|
| `Ui.BarChart(title, points)` / `new UiBarChart(points, title, valueSuffix)` | a few categories, last 7 days | <= 500 points, aim 7-12; non-empty labels |
| `UiLineChart(series, title, suffix)` | trend over time | 1-6 series, each <= 500 points, x order |
| `Ui.DonutChart(title, slices)` / `UiDonutChart(slices, title, centerValue, centerLabel)` | share of a whole | 1-6 slices, values >= 0 |
| `UiHeatmap(rowLabels, colLabels, values, title, suffix)` | weekday x hour | rows 1-12, columns 1-24, `Values[row][col]` finite and >= 0, exact dimensions |
| `UiTable(columns, rows)` with `UiTableColumn(header, alignment, monospace)` / `UiTableRow(cells)` | short fact lists | 1-12 columns, <= 500 rows, one cell per column; right-align and `Monospace` numbers |
| `UiProgress(value, max, label, tone)` | amount done | max > 0, 0 <= value <= max |

Pass a null chart title when the surrounding card already has one (Insights does). All counts count toward the 2000-node tree budget (points, rows, fields count as nodes) and 512 KiB serialized size. Keep charts to what the user needs; cap loops (`Take(n)`). No `UiGlyph` with a chart title as decoration.

## Forms, inputs, actions

- `Ui.Button(id, label, variant, icon)` (`UiButtonVariant`: Primary at most one per view, Secondary, Warning, Subtle; `IsEnabled` via record ctor: disable at limits like `goal > MinGoal`), `Ui.Toggle(id, label, isOn, description)` (receives `UiAction.Toggle`, `Value` "true"/"false"; persist yourself), `Ui.Form(id, submitLabel, fields...)` with `UiTextField(id, label, value, placeholder, maxLength<=2000)`, `UiNumberField(id, label, value, min, max, step)` (min <= value <= max, step > 0), `UiSelectField(id, label, options, selectedValue)` (`UiOption(value, label)`, selected must exist), `UiCheckboxField`. Submit arrives as `UiAction.Submit` with string `FormValues` keyed by field id.
- Ids: `^[A-Za-z][A-Za-z0-9_-]{0,63}$`, unique in the whole tree (buttons, toggles, forms and fields share one namespace); use slug prefix + hyphens, no dots: `goals-add`, `goals-add-text`, `goals-del-12`.
- In `OnActionAsync`: check `action.Kind` first, then the id (exact constants or prefix + `int.TryParse`), then validate and clean values (trim, strip control chars, clamp length/range, enforce max items); ignore anything unexpected, never throw for user input. After a change, persist, raise `Changed`, `RequestRender()`.
- Copy: buttons are verbs ("Add", "Clear completed"), labels are nouns, sentence case, no exclamation marks or shaming, plural-correct counts. Plain text only; no control characters.

## Empty and example states

- Every data-driven card needs an empty state: a caption (`Ui.Text(text, UiTextStyle.Caption)`) saying what will appear and when ("Completed tasks show up here."), not an empty chart.
- For event-driven data (nothing to see before the first session) offer an example like Insights: empty page shows a card with explanation + `Ui.Button(ExampleButtonId, "Show example", UiButtonVariant.Secondary)`; the preview adds a caption row ("Example data — your own stats appear after your first session.") with a "Hide example" button, and renders the same layout function with generated sample data. Keep the flag in memory (`Volatile`), never write sample data to storage, cache the generated example per day/zone.
- Share one pure layout function between real and example data so they cannot drift.

## Settings section (`ISettingsSectionPlugin`, `ui.settings`)

- `string Title { get; }` (section heading, drawn by the host) and `RenderAsync` returning any `UiNode` (existing sections return `Ui.Column(...)`; not a `UiPage`, no heading text repeated).
- Pattern: a short explanation (`Caption`), the controls (toggles with descriptions, +/- buttons or a small form), then dangerous actions last. Keep to decisions the user must make.
- Destructive actions (clear data, reset) need a second click, as in the bundled plugins' settings sections: first click arms (store `Time.GetTimestamp()` and a flag with `Volatile`, `RequestRender()`), the button then reads "Click again to confirm" with `UiButtonVariant.Warning` and `UiIcon.Warning`; a second click within the window (`ConfirmWindow` = 10 s, checked with `Time.GetElapsedTime`) performs the action; after the window the button reverts. If the action changes nothing, still `RequestRender()` so the button leaves the armed state. Describe what will be deleted in a caption above.
- Privacy-relevant recording options (e.g. storing app names) are off or clearly described toggles.

## Validity and tests

`ctx.RenderAsync(...)` throws `UiValidationException` (and checks the JSON round trip) when a tree breaks any limit; render page, settings and the example/empty variants in tests, assert the headline text/values exactly, assert no `UiGlyph` and that the page tree has no repeated title, test both clicks of every confirm button (use `ctx.FakeTime.Advance` past the window), invalid form input, and storage content after actions.

## Checklist

- [ ] `Ui.Page(title, subtitle, cards)`; nothing repeats title/logo in content; nav title <= 32.
- [ ] Cards with noun titles, no nesting; stats in a grid; charts within limits.
- [ ] Empty state for each card; example mode when data is event-driven.
- [ ] Ids unique, hyphenated, no dots; inputs validated; one Primary button max.
- [ ] Destructive settings need a second click; tests cover both clicks and the timeout.

Note: bundled plugins inside the WindowsDetox repository follow the repository's own skills; this copy is for standalone plugins authored from the starter template.
