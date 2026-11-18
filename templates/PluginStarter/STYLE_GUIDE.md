# Blocker plugin UI style guide

Plugins never draw. They return a tree of declarative blocks (`Blocker.UI.Sdk`, built with the `Ui` factory) and Blocker
renders it in its own style: deep-black monochrome surfaces with hairline borders, olive accent and sage highlights, Segoe UI
Variable for text and Cascadia Mono for numbers. Your plugin looks native automatically; your job is to choose the right
block and the right words.

## Block catalogue

| Block (factory) | Use it for | Notes |
|---|---|---|
| `UiPage` (`Ui.Page(title, subtitle?, ...)`) | Root of a full page (`ui.page`). | Only as root. Children are stacked vertically. |
| `UiCard` (`Ui.Card(title, ...)`, `Ui.Card(...)`) | Grouping on pages; the mandatory root of a dashboard widget. | Optional `Subtitle` and `Icon` via the record constructor. Do not nest cards in cards. A widget's card has no title (`Ui.Card(children)`): the host draws the plugin icon and name. |
| `UiStack` (`Ui.Column`, `Ui.Row`) | Vertical or horizontal grouping. | `Gap`: Small, Medium (default), Large. An empty stack renders nothing (use it to hide a contribution). |
| `UiGrid` (`Ui.Grid(columns, ...)`) | Equal-width tiles, 1 to 6 columns. | Use 2 or 3 columns for stat tiles. |
| `UiText` (`Ui.Text(text, style, tone)`) | Explanations, labels, status lines. | Styles: Body, Caption (secondary), Subtitle, Title, Mono (numbers, codes). Plain text only. |
| `UiStatTile` (`Ui.Stat(label, value, caption?, tone, icon?)`) | One headline number. | Value is pre-formatted text (`"3 h 12 min"`). Keep the value short. |
| `UiProgress` (`Ui.Progress(value, max, label?)`) | Elapsed or remaining amount. | `0 <= value <= max`, `max > 0`, both finite. |
| `UiBarChart` (`Ui.BarChart(title, points)`) | Compare a few categories. | Up to 500 points, aim for 7 to 12. `ValueSuffix` for a unit. |
| `UiLineChart` | Trend over time. | 1 to 6 series, points in x order. |
| `UiDonutChart` (`Ui.DonutChart(title, slices)`) | Share of a whole (for example time per app category). | 1 to 6 slices, non-negative; optional `CenterValue` and `CenterLabel`. All zero shows an empty ring. |
| `UiHeatmap` (`new UiHeatmap(rowLabels, columnLabels, values)`) | Activity by two dimensions (weekday by hour). | Up to 12 rows and 24 columns, `Values[row][column]` finite and non-negative; empty column labels keep the axis quiet. |
| `UiGlyph` (`Ui.Glyph(pathData, size)`) | Your plugin's own vector logo. | Path commands M L H V C S Q T A Z only, up to 4096 characters, size 12 to 96. Coloured by the host (`Tone`, default Accent). |
| `UiTable` | Short lists of facts. | 1 to 12 columns, every row has one cell per column, up to 500 rows. Use `Monospace` for numeric columns. |
| `UiButton` (`Ui.Button(id, label, variant, icon?)`) | One action. | Variants: Primary (one per view at most), Secondary, Warning (caution), Subtle (low emphasis, e.g. Reset). `IsEnabled` via the record. |
| `UiToggle` (`Ui.Toggle(id, label, isOn, description?)`) | A persisted on/off setting. | Receives `UiAction.Toggle`; persist the new value yourself. |
| `UiForm` (`Ui.Form(id, submitLabel, fields)`) | Several inputs submitted together. | Fields: text, number, select, checkbox. Values arrive as strings in `FormValues`; validate them. |

## Tones (semantic only)

`Neutral` default; `Accent` the plugin's focal item; `Success` good outcome (streaks, resisted distractions); `Warning` needs attention;
`Danger` irreversible or failing. Choose a tone for its meaning, never for decoration. At most one accent per card.

## Icons (`UiIcon`)

Clock, Timer, Shield, Lock, Unlock, Play, Pause, Stop, Warning, Info, Checkmark, Dismiss, Settings, Chart, Calendar, Apps, Globe,
Plug, Flame, Leaf, Target, Star, Heart, Book, Code, Music, Game, Moon, Sun, Coffee, Folder, Trophy.
Use an icon to support a label, not to replace it. Unlisted glyphs and emoji are not icons.

## Layout guidance

* Dashboard widget: always one title-less `UiCard` (`Ui.Card(children)`); the host draws the surface, the plugin icon and name, and
  opens the plugin page on tap. One primary `UiStatTile`, at most one `UiProgress`, at most three rows, a one-line empty state, no
  `UiGlyph` and no nested cards; it must read at one cell (about 260 x 140 px). `Small` = one stat; `Medium` = stat plus progress or
  one quiet button; `Wide` = room for rows, never new meaning. Full rules: `.claude/skills/windowsdetox-widget/SKILL.md`.
* Order inside a card: headline stat, supporting visual (progress/chart), actions last.
* Gaps: keep the default `Medium`; use `Small` for tightly related items (label + value), `Large` between unrelated groups.
* Settings section: a column of toggles and forms, each with a short description. Keep it to what the user must decide.
* Block screen: one or two lines. It is shown at a stressful moment; never show buttons that nag or shame.
* Pages: title, one-sentence subtitle, then cards; a page should be scannable in five seconds.

## Limits (enforced by `UiTreeValidator`, `UiLimits.Default`)

| Limit | Value |
|---|---|
| Nesting depth | 16 |
| Nodes per tree (including fields, points, rows) | 2000 |
| Length of any text | 2000 characters |
| Chart points, table rows, select options per element | 500 |
| Grid columns | 1 to 6 |
| Table columns | 1 to 12 |
| Line chart series | 1 to 6 |
| Navigation title | 1 to 32 characters |
| Serialized tree | 512 KiB |
| Element ids | `^[A-Za-z][A-Za-z0-9_-]{0,63}$`, unique per tree |
| Text field `MaxLength` | 1 to 2000 |

An invalid tree is not rendered at all. In tests, `RenderAsync` throws `UiValidationException` listing the problems.

## Copywriting

* Short, calm, factual. Sentence case, no exclamation marks, no shaming ("You failed"), no pressure.
* Labels are nouns ("Distractions resisted"); buttons are verbs ("Reset counter"); toggle labels name the thing ("Show on block screen").
* Numbers are pre-formatted for people: `"1 h 05 min"`, not `"3900"`. Pluralise correctly ("1 distraction", "2 distractions").
* Captions explain scope ("Since the session started"). Say what a button does, not "Click here".
* Use the user's language consistently within one plugin. No jargon such as "callback", "snapshot", "IPC".

## Prohibitions

* No custom colours, fonts, sizes, XAML, images, HTML or Markdown: text is plain and never interpreted as markup.
* No emoji used as icons (use `UiIcon`), no ASCII art, no repeated characters as bars (use `UiProgress`).
* No control characters in any string (only `\n` and `\t` are tolerated); strip them from external data before displaying it.
* Do not simulate native controls with text, and do not try to read or display window titles without `events.window.title`.
* Do not render secrets, file paths of the user, or anything you would not show over the user's shoulder.
