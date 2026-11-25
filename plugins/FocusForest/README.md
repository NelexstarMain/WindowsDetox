# Focus Forest

First-party Blocker plugin (`com.windowsdetox.forest`, version 1.0.0). Every finished detox session grows a small isometric forest.
It was split out of Focus Insights 1.1.0; no data migration is needed because the forest had shipped only hours earlier.

## How it works

* A session that runs to its planned end earns 1 coin per minute and plants one tree or bush of the species chosen under "Planting next" (default oak). The stage follows the real session length: under 25 min sapling, 25-49 young, 50-89 mature, 90+ majestic.
* A session ended with break-glass earns no coins and plants a withered plant of that species; it can be removed for 10 coins. Other ends change nothing.
* Shop: trees and bushes are unlocked once with coins and then selectable; decorations are bought per piece, then placed by choosing them in the inventory and clicking an empty tile.
* New plants take the free tile nearest to the centre. Click a plant or decoration, choose Move, then click an empty tile to move it (a moved object is pinned).
* The field starts at 6x6 and grows by 2 (up to 16x16) when it is 75% full; objects stay centred. A full 16x16 field plants nothing more (coins are still earned).

## What it shows

* Page "Forest": stat tiles (coins, plants, streak, field size); the big forest scene with a hint line and the "Arrange world" buttons; a selection panel when an object is clicked; "Planting next" (species chips); the inventory of owned decorations; the shop (tabs Trees, Bushes, Decorations with thumbnails, prices and states). An empty forest shows how the forest grows and a small preview instead of the scene.
* Selection panel: name, stage, planted date, session minutes, pinned state and the buttons Move (then click an empty tile; the object ends up pinned), Pin/Unpin, Remove (withered plants, for the fee) and Close. A growing sapling says it finishes when the session ends.
* "Arrange world" lays the world out again and keeps pins; "Release all pins and arrange" asks for a second click within 10 seconds.
* Dashboard widget (small): plants, coins and a mini forest; tapping it opens the page.
* Settings "Focus Forest": "Reset forest" (asks for a second click within 10 seconds) starts a fresh forest.

## Data

Plugins are isolated, so the forest tracks sessions itself: the running session (id, start, planned length) is stored with the forest, survives a restart, and the actual length is the time between the start and end events. An end without a matching start (plugin installed mid-session) is ignored. Everything lives under the storage key `forest` (versioned compact JSON, at most 256 objects); an unreadable document is kept once under `forest-unreadable`, and a newer format version is shown read-only.

## Capabilities

`events.session`, `ui.page`, `ui.dashboard`, `ui.settings`. No network, no process or window events.

## Files

`plugin.json`, `ForestPlugin.cs` (entry point, logo, page, widget, settings), `ForestStore.cs` (game rules, session tracking, persistence), `ForestSpecies.cs` (catalogue, stages), `ForestModels.cs` and `ForestMeshes.cs` (scene models), `ForestLayout.cs` (page body, sample forest), `ForestTests.cs`, `ForestUiTests.cs` and `ForestModelsTests.cs` (run by Blocker before installing). The folder is zipped flat; `BundledPluginTests` runs it through package, policy, compile and plugin tests.
