---
name: windowsdetox-plugin
description: End-to-end workflow for creating a new WindowsDetox plugin (bundled in plugins/ or a standalone ZIP). Use when asked to add, write or scaffold a "plugin", "wtyczka", "nowa wtyczka", "rozszerzenie", plugin.json, capabilities, storage, tests with FakePluginContext, bundling into the installer, or screenshots of a plugin page ("zrzut ekranu", "screenshots.ps1"). Pair with windowsdetox-plugin-icon, windowsdetox-widget and windowsdetox-plugin-page.
---

# WindowsDetox plugin: create one end to end

Source of truth is the code. Contract for uploaded plugins: `templates/PluginStarter/AGENT_INSTRUCTIONS.md` and `STYLE_GUIDE.md`. Reference plugins: `plugins/GoalsTasks` (small) and `plugins/FocusInsights` (events + charts). Copy their structure.

## 1. Folder layout (bundled plugin)

`plugins/<PascalName>/` (flat, no subfolders; the installer zips only top-level `.cs/.json/.md`):

| File | Content |
|---|---|
| `plugin.json` | manifest (see 2) |
| `<X>Plugin.cs` | `IBlockerPlugin` entry + page/widget/settings classes + a shared `abstract class XContribution : IUiContribution` that stores `IUiSurface` and re-renders on `store.Changed` |
| `<X>Store.cs` | limits, persisted records, in-memory cache, write-through persistence |
| `<X>View.cs` | pure aggregation/formatting (no state, no I/O) |
| `<X>Tests.cs` | `[Fact]`/`[Theory]+[InlineData]` tests, namespace `<X>.Tests` |
| `README.md` | what it stores, shows, capabilities, files |

A standalone ZIP is the same files at the ZIP root. Limits: ZIP <= 5 MB, <= 50 entries, <= 2 MB unpacked, ratio <= 100, only `.cs .json .md`, UTF-8, path depth <= 3 (`PluginPackageReader`). Compiler global usings: only `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`; add every other `using` yourself. No NuGet.

## 2. plugin.json (`PluginManifestParser`; unknown or duplicate keys are errors, file <= 16 KB)

Required: `id`, `name`, `version`, `author`, `sdkVersion`, `entryType`, `capabilities` (use `[]` if none). Optional: `description`, `icon`.
- `id`: `PluginId` rule `^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*){1,4}$`, 2-5 dot segments, <= 128. Bundled ones use `com.windowsdetox.<slug>`. Id is the storage and IPC namespace: never change it after release.
- `name` and `author`: 1-64 chars, no control chars. `description`: <= 500 chars, plain text, say what it does for the user.
- `version`: `major.minor.patch` (digits only, <= 9 each). `sdkVersion`: `"1.0"` (`BlockerSdk.Version` is 1.0.0; newer minor/major is refused).
- `entryType`: `Namespace.Class`, public, implements `IBlockerPlugin`, public parameterless ctor.
- `icon`: SVG path data, see skill `windowsdetox-plugin-icon`. Every plugin with `ui.page` needs a unique one.

## 3. Capabilities: request only what the code uses

Names (`PluginCapabilities`): `events.session`, `events.process`, `events.window`, `events.window.title` (privacy-sensitive, avoid), `events.idle`, `events.custom`, `intercept.process`, `intercept.window`, `tick`, `lockpolicy`, `ipc`, `ui.page`, `ui.dashboard`, `ui.settings`, `ui.blockscreen`, `network`, `session.start` (`context.Sessions`), `notify` (`context.Notifier`).
- Implementing an interface needs its capability (BLK008 at compile time, `PluginCapabilityException` at runtime): `IUiPagePlugin`=`ui.page`, `IDashboardWidgetPlugin`=`ui.dashboard`, `ISettingsSectionPlugin`=`ui.settings`, `IBlockScreenPlugin`=`ui.blockscreen`, `ITickListener`=`tick`, `IProcessInterceptor`=`intercept.process`, `IWindowInterceptor`=`intercept.window`, `ILockPolicyContributor`=`lockpolicy`.
- `Events.Subscribe<T>`: `SessionStarted/SessionEnded/EmergencyPassStarted/EmergencyPassEnded`=`events.session`, `ProcessBlocked`=`events.process`, `ActiveWindowChanged`=`events.window`, `UserIdleChanged`=`events.idle`. `SubscribeCustom/PublishCustomAsync`=`events.custom`; `Ipc.Register`=`ipc`.
- Declared-but-unused capabilities are a defect: tests in `BundledPluginTests` assert the exact list.

## 4. Code rules

- Register everything in `InitializeAsync` via `context.Extensions.Add(...)` (one object may implement several interfaces; if two interfaces share a `RenderAsync` signature, e.g. settings + block screen, use separate classes). `ShutdownAsync` flushes the store.
- Clock: `context.Time` only (`GetUtcNow()`, `GetTimestamp()`, `GetElapsedTime`, `LocalTimeZone`). `DateTime.Now/UtcNow/Today`, `DateTimeOffset.Now/UtcNow`, `Stopwatch`, `TimeProvider.System` are BLK025. Day keys: `DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Time.GetUtcNow(), Time.LocalTimeZone).DateTime).DayNumber`.
- Storage (`context.Storage`, `IPluginStorage`): string values, keys `^[a-z0-9_-][a-z0-9._-]*$` <= 128, quota `QuotaBytes`; `WriteAsync` throws `PluginStorageException` over quota (catch it, log with `Logger.Warn`, mark dirty). Load once at start, treat loaded data as untrusted (sanitize), keep records small and bounded (retention days, max items), write through on change. Never read storage inside `RenderAsync`.
- Other denied APIs: file system (BLK020), `Process` (BLK010), `Environment` (BLK022), threads/blocking waits (BLK023/024), reflection invoke (BLK012), `Console` (BLK041), `dynamic` (BLK006), `async void` (BLK007), `unsafe`/interop. Network needs `network` and is not testable.
- Callbacks (render, action, event, vote) have a 50 ms budget (`BlockerSdk.CallbackBudgetMilliseconds`); repeated overruns or exceptions fault the plugin. Render from the in-memory snapshot; slow work goes to `ITickListener` or start-up.
- `InitializeAsync` and the host warm-up render of every contribution run under 20x the budget (1 s); the first render is JIT-heavy, so keep first-render work trivial. A contribution that exceeds the budget is left out of its slot.
- Action ids: `^[A-Za-z][A-Za-z0-9_-]{0,63}$`, unique per tree, hyphenated and prefixed with the plugin slug (`goals-add`, `goals-done-12`); NO dots, NO spaces. Parse dynamic suffixes with `int.TryParse(..., NumberStyles.None, CultureInfo.InvariantCulture)`. Validate `UiAction` kind, id and `FormValues` before acting; ignore the unexpected.
- After state changes raise `Changed` and call `IUiSurface.RequestRender()` (kept from the last render/action context). IPC method names: `^[a-z][A-Za-z0-9]{0,63}$`; payloads are untrusted JSON <= 64 KiB.
- Host UI limits (`UiTreeValidator`, `UiLimits.Default`, `UiJson.MaxDocumentBytes`): depth 16, 2000 nodes (fields/points/rows count), text 2000 chars without control chars, 500 items per chart/table/select, serialized tree 512 KiB. Invalid tree = nothing rendered. Design details: skills `windowsdetox-widget`, `windowsdetox-plugin-page`.

## 5. Tests (mandatory, run by the install pipeline and by CI)

`using Xunit; using Blocker.Plugin.Testing;` Only `[Fact]` and `[Theory]` with `[InlineData]`; no Skip/Explicit/MemberData/fixtures/`async void`; 5 s per test, 60 s total; public class, parameterless ctor, namespace `<X>.Tests`.
- `new FakePluginContext(pluginId, capabilities)` (no capabilities = all granted; starts 2026-01-01T09:00:00Z, UTC). Members: `FakeTime.Advance`, `FakeSession.Current`, `Storage`/`FakeStorage`, `Logger`/`FakeLogger.Entries`, `RegisteredExtensions`, `GetExtension<T>()`, `PublishAsync(evt)`, `InvokeIpcAsync`, `TickAsync`, `RenderAsync(contribution)` (validates + JSON round-trips like the host, throws `UiValidationException`), `SendActionAsync(contribution, UiAction.Click/Toggle/Submit)`, `RenderRequests`; `PluginTestData.Process(...)`, `PluginTestData.ActiveSession(...)`.
- Cover: missing capability (`PluginCapabilityException` with `.Capability`), registration list, storage format and reload after restart, sanitization of corrupt stored data, every action (valid + invalid input), empty state, limits, render validity of page/widget/settings, retention/day rollover with `FakeTime`. Exact assertions. Reference counts: Insights 39 `[Fact]`s, Goals >= 25.

## 6. Bundle it (do not forget)

1. `eng/build-installer.sh` zips every `plugins/*/` containing `plugin.json` to `bundled-plugins/<id>.zip` automatically; nothing to edit there.
2. `tests/Blocker.Core.Tests/Plugins/BundledPluginTests.cs` hard-codes the folder list: in `Every_bundled_plugin_folder_has_a_unique_id_and_icon_and_is_covered_by_these_tests` extend `Assert.Equal(["FocusInsights", "GoalsTasks"], folders)` (ordinal order) and add a `<Name>_passes_every_install_stage_and_all_of_its_tests` test (copy the Goals one: `ZipOfFolder("<Folder>")` -> `PluginInstallPipeline.EvaluateAsync` -> assert Ready stage, id, version, entryType, icon valid and different from the others, `tests.Passed >= N`, exact capability list). Icons and ids must be unique across folders.
3. On first start the service installs each zip once through the normal pipeline (`InstallBundledPluginsAsync`), waits while a session runs, retries 8x every 15 s, and records the zip file name in `%ProgramData%\WindowsDetox\bundled-plugins.txt`. A zip already listed is never offered again (a plugin the user removed stays removed). A fixed bug in an already-shipped bundled plugin therefore reaches existing installs only if the zip name changes, i.e. a new id: normally it does not; tell the user.

## 7. Build, test, gate (from repo root, .NET 10 SDK)

```
dotnet build WindowsDetox.slnx -c Release
dotnet test --project tests/Blocker.Core.Tests -c Release --filter-class "*BundledPluginTests"
dotnet test --solution WindowsDetox.slnx -c Release --no-build --filter-not-trait "Category=WindowsOnly"
dotnet tool restore && dotnet slopwatch analyze -d . --fail-on warning
python3 .claude/skills/windowsdetox-plugin-icon/preview_icon.py plugins/<Folder>/plugin.json
```
Warnings are errors. Slopwatch flags skipped tests, empty `catch`, suppressions; fix the cause. Plugin diagnostics print as `file:line: BLKxxx`.

## 8. Verify on Windows CI (UI is WPF, cannot run on Linux)

`eng/screenshots.ps1 -Phase app` (CI job `installer`) waits for the bundled-plugin marker count to equal the shipped zips, starts `Blocker.UI.exe` and clicks sidebar items by UIAutomation name via `Click-Named`. Add a shot for the new page next to `if (Click-Named $main 'Goals') { ... '16-goals' }`, before `Stop-Process $ui`:
`if (Click-Named $main '<Navigation title>') { Start-Sleep -Seconds 3; Shot $main '18-<slug>' }` (next free number; the name must equal `Navigation.Title` exactly and be unique in the left 300 px). Widgets appear in `13-session-with-widget`, `14-widget-picker`, `15-widgets-edit`, `17-widget-opens-plugin`. CI commits `shots/*.png` into `docs/screenshots` on push; review them and fix clipping, icon legibility and empty states.

## Checklist

- [ ] Unique id, icon, nav title; valid version/sdkVersion/entryType; description written for users.
- [ ] Capabilities exactly match used APIs; no denied API; no `DateTime.Now`; no `async void`; no empty `catch`.
- [ ] Renders read only cached state (< 50 ms); store bounded, sanitized on load, written through, flushed in `ShutdownAsync`.
- [ ] Action ids hyphenated, no dots, unique, parsed defensively; destructive actions need a second click.
- [ ] Page, widget, settings follow their skills; empty states and an example/preview where data is event-driven.
- [ ] Tests green with exact assertions; negative paths covered; README written.
- [ ] `BundledPluginTests` list + new install test updated; icon unique (`preview_icon.py`).
- [ ] Build, tests, slopwatch pass; `eng/screenshots.ps1` has a shot.
