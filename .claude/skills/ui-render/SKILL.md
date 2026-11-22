---
name: ui-render
description: Render the real WPF UI (main window pages, plugin pages and widgets, with fake data) to PNG files on Linux under Wine and look at them. Use to check a UI or plugin layout change visually, or when asked for a screenshot or "how does it look".
---

# UI render harness (Linux, Wine)

`tools/Blocker.UI.Render` builds the real `MainWindow`, pages and view models on a stub service connection, runs the bundled
plugins (Calendar, Goals & Tasks, Focus Insights) in-process with seeded data and a frozen clock (Monday 2026-11-23 10:30), and
renders each scenario with `RenderTargetBitmap` at 96 DPI. One process renders all shots.

## Run
```bash
eng/render.sh                          # all shots -> artifacts/renders/<name>.png (about 8 s once published)
eng/render.sh --only calendar-week,goals
eng/render.sh --out /tmp/shots
```
The first run publishes the harness, creates the Wine prefix, installs fonts (Inter stands in for Segoe UI) and starts Xvfb :99
and a warm wineserver; later runs republish only when sources under `src/`, `plugins/` or the harness changed. Wine always runs
under `env -i` (a large environment crashes it). The per-shot timing and `render.log` end up in the output folder.

## Look
Open the PNGs with the Read tool (`artifacts/renders/dashboard-idle.png`, ...). Check that the theme is applied, text is drawn,
nothing is blank or clipped. Fonts differ from Windows, so judge layout and colour, not exact text width.

## Add a shot
Edit `tools/Blocker.UI.Render/scenarios.json` (no republish needed):
```json
{ "name": "calendar-week", "target": "plugin:calendar", "size": [1280, 1700], "session": "active",
  "steps": [ { "action": "cal-view-week" } ] }
```
- `target`: `main:dashboard|allowlist|setup|plugins|settings` or `plugin:calendar|goals|insights`; `session`: `idle|active|pass`.
- Steps: `{"action": "<UiAction id>", "args": {form fields}}` (runs the view's command, like a click; `args` makes it a submit,
  `"kind": "toggle", "value": "false"` a toggle), `{"click": [x, y]}`, `{"drag": [[x1, y1], [x2, y2]]}` (real injected mouse
  input at window coordinates), `{"wait": ms}`. Every shot starts from fresh plugin state.
- Seed data lives in `tools/Blocker.UI.Render/PluginSeeds.cs`, canned service answers in `StubServiceConnection.cs`.
