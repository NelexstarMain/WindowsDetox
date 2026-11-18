# Focus Insights

First-party Blocker plugin (`com.windowsdetox.insights`, version 1.0.0). The base app keeps no statistics on purpose; this plugin records them and shows them.

## What it records

All data stays on this device, in the plugin's private storage, as compact per-day aggregates (90 days, at most 30 recent sessions):

* sessions: start, planned length, actual length and how they ended (completed, break-glass, other)
* blocked attempts: count, two-hour time-of-day bucket and (optionally) the app name, at most 30 distinct apps per day
* emergency passes used

Blocked attempts are written to storage at most every 10 seconds (and on shutdown); session events are written immediately.

## What it shows

* Page "Insights": logo header, four stat tiles (focus time and blocked attempts of the last 7 days, sessions completed, current streak), focus minutes per day (14 days), most blocked apps (30 days), session outcomes donut, a weekday by time-of-day heatmap of blocked attempts and the last 8 sessions. Before the first session it shows a friendly empty state with a "Show example" button that previews every chart with deterministic sample data (nothing is saved).
* Dashboard widget (small): today's focus minutes with the last-7-days total; the host draws the surface, icon and name, and tapping it opens this page.
* Settings: "Record blocked app names" (privacy, on by default) and "Clear recorded data" (asks for a second click).

## Capabilities

`events.session`, `events.process`, `ui.page`, `ui.dashboard`, `ui.settings`. No network, no window titles, no idle events.

## Files

`plugin.json`, `InsightsPlugin.cs` (entry point, logo, page, widget, settings), `InsightsStore.cs` (recording, retention, persistence), `InsightsView.cs` (aggregation, example data, formatting), `InsightsTests.cs` (run by Blocker before installing). The folder is zipped flat; the install pipeline test `BundledPluginTests` runs it through package, policy, compile and plugin tests.

Notes: days and hours use the local time zone at the time of the event; a session counts for the day it started; the streak counts consecutive days with a completed session (up to the 90 retained days).
