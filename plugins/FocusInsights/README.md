# Focus Insights

First-party Blocker plugin (`com.windowsdetox.insights`, version 1.1.0). The base app keeps no statistics on purpose; this plugin records them and shows them.

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

## Focus Forest

Every session also grows a small isometric forest, drawn on the page's "Forest" tab (the statistics are on the "Stats" tab; "Show example" previews both).

* A completed session earns 1 coin per minute and plants one tree or bush of the species chosen under "Planting next" (default oak). The stage follows the session length: under 25 min sapling, 25-49 young, 50-89 mature, 90+ majestic.
* A session ended with break-glass earns no coins and plants a withered plant of that species; it can be removed for 10 coins. Other ends change nothing.
* Shop: trees and bushes are unlocked once with coins and then selectable; decorations are bought per piece, then placed by choosing them in the inventory and clicking an empty tile.
* New plants take the free tile nearest to the centre. Click a plant or decoration, then an empty tile, to move it (click it again or "Cancel" to deselect).
* The field starts at 6x6 and grows by 2 (up to 16x16) when it is 75% full; objects stay centred. A full 16x16 field plants nothing more (coins are still earned).
* Stored under the key `forest` (versioned compact JSON, at most 256 objects). "Clear recorded data" in settings clears statistics only, never the forest.

## Capabilities

`events.session`, `events.process`, `ui.page`, `ui.dashboard`, `ui.settings`. No network, no window titles, no idle events.

## Files

`plugin.json`, `InsightsPlugin.cs` (entry point, logo, page, widget, settings), `InsightsStore.cs` (recording, retention, persistence), `InsightsView.cs` (aggregation, example data, formatting), `ForestSpecies.cs` (catalogue, stages), `ForestModels.cs` and `ForestMeshes.cs` (scene models), `ForestStore.cs` (game rules, persistence), `ForestLayout.cs` (forest tab, example forest), `InsightsTests.cs` and `ForestTests.cs` (run by Blocker before installing). The folder is zipped flat; the install pipeline test `BundledPluginTests` runs it through package, policy, compile and plugin tests.

Notes: days and hours use the local time zone at the time of the event; a session counts for the day it started; the streak counts consecutive days with a completed session (up to the 90 retained days).
