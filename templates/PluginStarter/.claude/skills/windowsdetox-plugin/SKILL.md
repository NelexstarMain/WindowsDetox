---
name: windowsdetox-plugin
description: End-to-end workflow for creating a new WindowsDetox plugin (standalone ZIP built from the starter template). Use when asked to add, write or scaffold a "plugin", "wtyczka", "nowa wtyczka", "rozszerzenie", plugin.json, capabilities, storage, tests with FakePluginContext. Pair with windowsdetox-plugin-icon, windowsdetox-widget and windowsdetox-plugin-page.
---

# WindowsDetox plugin: create one end to end

## 1. Folder layout (this template folder)

Source of truth for the API contract: `AGENT_INSTRUCTIONS.md` and `STYLE_GUIDE.md` in this folder. `PluginMain.cs` and `PluginTests.cs` are a working small plugin; edit them in place.

| File | Content |
|---|---|
| `plugin.json` | manifest (see 2) |
| `PluginMain.cs` | `IBlockerPlugin` entry + page/widget/settings classes; split larger plugins into a store (limits, persisted records, in-memory cache, write-through persistence), a pure view/formatting file and a shared `abstract class XContribution : IUiContribution` that stores `IUiSurface` and re-renders on `store.Changed` |
| `PluginTests.cs` | `[Fact]`/`[Theory]+[InlineData]` tests, namespace `<X>.Tests` |
| `README.md` | what it stores, shows, capabilities, files |

Keep `.cs`/`.json`/`.md` files flat in the folder root. The `.claude/` folder (these skills) is authoring tooling: Blocker ignores it on upload.

The ZIP is these files at its root. Limits: ZIP <= 5 MB, <= 50 entries, <= 2 MB unpacked, ratio <= 100, only `.cs .json .md`, UTF-8, path depth <= 3 (`PluginPackageReader`). Compiler global usings: only `System`, `System.Collections.Generic`, `System.Linq`, `System.Threading`, `System.Threading.Tasks`; add every other `using` yourself. No NuGet.

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
- Declared-but-unused capabilities are a defect: test the exact list.

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
- Cover: missing capability (`PluginCapabilityException` with `.Capability`), registration list, storage format and reload after restart, sanitization of corrupt stored data, every action (valid + invalid input), empty state, limits, render validity of page/widget/settings, retention/day rollover with `FakeTime`. Exact assertions. Aim for thorough coverage (the bundled Focus Insights plugin has 39 tests).

## 6. Package and install

Follow the exact steps in `AGENT_INSTRUCTIONS.md` section 8 (Packaging) and `README.md`: make sure `plugin.json` is valid and the `version` is higher than the installed one, zip the **contents** of this folder (not the folder itself; `plugin.json` at the ZIP root), then choose the ZIP on Blocker's Plugins page. Blocker validates, compiles, analyses and runs `PluginTests.cs` on install; any failure blocks installation and is shown as `file:line: BLKxxx`. Installing is blocked during an active detox session. Do not rename the plugin `id` once released.

## 7. Check locally (optional)

Only when a .NET 10 SDK is available: see `AGENT_INSTRUCTIONS.md` section 7 for a local test project. Otherwise rely on the in-app install tests. Warnings are errors in the plugin compiler. Do not skip tests, leave empty `catch` blocks or suppress diagnostics; fix the cause.

Icon preview: `python3 .claude/skills/windowsdetox-plugin-icon/preview_icon.py plugin.json`

Bundled plugins inside the WindowsDetox repository follow the repository's own skills; this copy is for standalone plugins.

## Checklist

- [ ] Unique id, icon, nav title; valid version/sdkVersion/entryType; description written for users.
- [ ] Capabilities exactly match used APIs; no denied API; no `DateTime.Now`; no `async void`; no empty `catch`.
- [ ] Renders read only cached state (< 50 ms); store bounded, sanitized on load, written through, flushed in `ShutdownAsync`.
- [ ] Action ids hyphenated, no dots, unique, parsed defensively; destructive actions need a second click.
- [ ] Page, widget, settings follow their skills; empty states and an example/preview where data is event-driven.
- [ ] Tests green with exact assertions; negative paths covered; README written.
- [ ] Icon previewed and distinct (`preview_icon.py`).
- [ ] ZIP made from the folder contents, version bumped, installs on the Plugins page and its tests pass.
