# Blocker plugin: instructions for AI agents

You are editing a Blocker plugin. Follow this document exactly; it is the contract. The UI rules are in
`STYLE_GUIDE.md`. The user will zip this folder and upload it in Blocker, which validates, compiles, analyses and
tests it before installing. A plugin that does not pass is not installed, so correctness beats cleverness.

## 1. File layout

```
plugin.json            manifest (required, at the ZIP root)
PluginMain.cs          entry point and extensions (any number of .cs files may be added)
PluginTests.cs         tests (required; at least one [Fact]/[Theory] must exist and ALL must pass)
AGENT_INSTRUCTIONS.md  this file (optional in the ZIP)
STYLE_GUIDE.md         UI rules (optional in the ZIP)
README.md              human notes (optional in the ZIP)
```

* Allowed file types: `.cs`, `.json`, `.md`. Anything else is rejected.
* Limits: at most 50 files, 2 MB unpacked. No folders that escape the root (`..`), no absolute paths.
* Language: C# (latest), nullable enabled, namespace-per-plugin. Use explicit `using` directives (no implicit usings).
* Only the Blocker SDK and the .NET base class library are referenced. NuGet packages and other DLLs are impossible.

## 2. Manifest (`plugin.json`)

| Field | Type | Required | Rules |
|---|---|---|---|
| `id` | string | yes | Reverse-DNS, 2 to 5 dot-separated segments, each `[a-z][a-z0-9-]*`, max 128 chars. Example `com.example.focus-counter`. Change it for your own plugin; it is also the storage and IPC namespace. |
| `name` | string | yes | Display name, short, plain text. |
| `version` | string | yes | `major.minor.patch`, for example `1.0.0`. Increase it for every upload. |
| `author` | string | yes | Display name of the author. |
| `description` | string | yes | One or two plain sentences: what the plugin does for the user. |
| `sdkVersion` | string | yes | SDK contract version the plugin was written for. Currently `"1.0"`. A newer major version is refused. |
| `entryType` | string | yes | Fully qualified name of the public class implementing `IBlockerPlugin`, with a public parameterless constructor. Example `FocusCounter.PluginMain`. |
| `capabilities` | string[] | yes | Exactly the capabilities the code uses (see table). Unknown names are rejected. Declare the minimum: the user sees this list before installing. Use `[]` if none are needed. |

### Capabilities

| Manifest name | Grants |
|---|---|
| `events.session` | `SessionStarted`, `SessionEnded`, `EmergencyPassStarted`, `EmergencyPassEnded` |
| `events.process` | `ProcessBlocked` |
| `events.window` | `ActiveWindowChanged` (title is always `null`) |
| `events.window.title` | `WindowDescriptor.Title` is filled in. Without it the title is `null`. Never log or store titles. |
| `events.idle` | `UserIdleChanged` |
| `events.custom` | `IEventBus.PublishCustomAsync`, `CustomPluginEvent` |
| `intercept.process` | implement `IProcessInterceptor` |
| `intercept.window` | implement `IWindowInterceptor` (add `events.window.title` to also read titles) |
| `tick` | implement `ITickListener` |
| `lockpolicy` | implement `ILockPolicyContributor` |
| `ipc` | `IIpcRegistry.Register` |
| `ui.page` | implement `IUiPagePlugin` |
| `ui.dashboard` | implement `IDashboardWidgetPlugin` |
| `ui.settings` | implement `ISettingsSectionPlugin` |
| `ui.blockscreen` | implement `IBlockScreenPlugin` |
| `network` | outbound network access (host allow-listed; not available in tests) |

Using an API without its capability is a compile-time analysis error and, at runtime, throws
`PluginCapabilityException`. Keep the manifest and the code in sync: declare exactly what the code uses, nothing more.
`SubscribeCustom` observes other plugins' topics and needs no capability of its own.

## 3. Lifecycle

1. The host creates the `entryType` class (parameterless constructor, do nothing there).
2. `InitializeAsync(context, ct)` runs once: load storage into memory, subscribe to events, register extensions and IPC.
3. Callbacks run on thread-pool threads, possibly concurrently: protect shared state (`Interlocked`, `lock`, immutable snapshots).
4. `ShutdownAsync(ct)` runs once before unloading: flush state. Registrations are removed automatically.

Circuit breaker: every synchronous callback (event handler, interceptor, vote, render, action) has a **50 ms budget**
(`BlockerSdk.CallbackBudgetMilliseconds`). Repeated overruns or exceptions move the plugin to `Faulted` and it stops running.
Therefore: render from cached memory, never read storage or compute heavily inside `RenderAsync`; do slow work in
`ITickListener.OnTickAsync` or at start-up. Catch only exceptions you can handle; never swallow with an empty `catch`.

## 4. SDK reference (exact signatures)

```csharp
namespace Blocker.Sdk;
public interface IBlockerPlugin {
    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);
    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}
public interface IPluginContext {
    PluginInfo Plugin { get; }            // Id, Name, Version, Author, Capabilities, Has(PluginCapability)
    IEventBus Events { get; }
    IExtensionRegistry Extensions { get; }
    IIpcRegistry Ipc { get; }
    IPluginStorage Storage { get; }
    ISessionInfo Session { get; }         // Session.Current -> SessionSnapshot
    TimeProvider Time { get; }            // the ONLY clock: Time.GetUtcNow(), Time.GetTimestamp()
    IPluginLogger Logger { get; }
}
public static class BlockerSdk { const int CallbackBudgetMilliseconds = 50; const int MaxPayloadBytes = 64 * 1024; }
public sealed class PluginCapabilityException : InvalidOperationException { PluginCapability Capability { get; } }

namespace Blocker.Sdk.Events;
public interface IEventBus {
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler) where TEvent : class, IBlockerEvent;
    IDisposable SubscribeCustom(string topic, Func<CustomPluginEvent, CancellationToken, ValueTask> handler);
    ValueTask PublishCustomAsync(string eventName, string payloadJson, CancellationToken cancellationToken = default);
}
// Event name: 1-64 chars of letters, digits, '-', '_', '.'. Payload: valid JSON, at most 64 KiB.

namespace Blocker.Sdk.Storage;
public interface IPluginStorage {
    long QuotaBytes { get; }
    ValueTask<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(string key, string value, CancellationToken cancellationToken = default);   // PluginStorageException when over quota
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
// Keys: 1-128 chars of a-z 0-9 . _ - (not starting with '.'). Helpers: ReadJsonAsync<T>, WriteJsonAsync<T> (camelCase JSON).

namespace Blocker.Sdk.Session;
public enum SessionPhase { Idle, Active, EmergencyPass }
public sealed record SessionSnapshot(SessionPhase Phase, Guid? SessionId, TimeSpan? Remaining, TimeSpan? EmergencyPassRemaining,
    int EmergencyPassesLeftToday, int EmergencyPassesPerDay, TimeSpan EmergencyPassDuration) { bool IsSessionRunning { get; } }

namespace Blocker.Sdk.Ipc;
public interface IIpcRegistry {   // capability: ipc. Callable as "plugin.{pluginId}.{method}"
    IDisposable Register(string method, Func<string, CancellationToken, ValueTask<string>> handler);   // method: ^[a-z][A-Za-z0-9]{0,63}$
}

namespace Blocker.Sdk.Diagnostics;
public interface IPluginLogger { void Log(PluginLogLevel level, string message, Exception? exception = null); }
// Extensions: logger.Info(msg), logger.Warn(msg), logger.Error(msg, ex). Rate limited; never log secrets or window titles.

namespace Blocker.Sdk.Extensions;
public interface IExtensionRegistry { IDisposable Add(IPluginExtension extension); }   // one object may implement several interfaces
public interface IProcessInterceptor : IPluginExtension {   // intercept.process
    ValueTask<InterceptDecision> OnProcessStartingAsync(ProcessDescriptor process, CancellationToken cancellationToken);
}
public interface IWindowInterceptor : IPluginExtension {    // intercept.window
    ValueTask<InterceptDecision> OnWindowActivatedAsync(WindowDescriptor window, CancellationToken cancellationToken);
}
// InterceptDecision.Abstain | InterceptDecision.Block(string reason). There is NO allow verdict.
public interface ITickListener : IPluginExtension {         // tick
    TimeSpan Interval { get; }
    ValueTask OnTickAsync(TickContext context, CancellationToken cancellationToken);   // TickContext(DateTimeOffset Now, SessionSnapshot Session)
}
public interface ILockPolicyContributor : IPluginExtension { // lockpolicy
    ValueTask<LockVote> EvaluateAsync(LockAction action, CancellationToken cancellationToken);
}
// LockAction: UninstallApplication, RemovePlugin, DisablePlugin, ChangeProtectionSettings, UseEmergencyPass.
// LockVote.Allow | LockVote.Veto(string reason).

namespace Blocker.Sdk.Model;
public sealed record ProcessDescriptor(int ProcessId, int ParentProcessId, string ImageName, string ExecutablePath,
    string? Sha256, string? PackageFamilyName, DateTimeOffset StartedAt);
public sealed record WindowDescriptor(int ProcessId, string ImageName, string ExecutablePath, string? PackageFamilyName, string? Title);

namespace Blocker.UI.Sdk;
public interface IUiContribution : IPluginExtension {
    ValueTask OnActionAsync(UiAction action, UiRenderContext context, CancellationToken cancellationToken);   // default: no-op
}
public interface IUiPagePlugin : IUiContribution {          // ui.page
    UiNavigationItem Navigation { get; }                    // UiNavigationItem(string Title /*<= 32*/, UiIcon Icon)
    ValueTask<UiPage> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}
public interface IDashboardWidgetPlugin : IUiContribution { // ui.dashboard
    UiWidgetSize Size { get; }                              // Small, Medium, Wide
    ValueTask<UiCard> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}
public interface ISettingsSectionPlugin : IUiContribution { // ui.settings
    string Title { get; }
    ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}
public interface IBlockScreenPlugin : IUiContribution {     // ui.blockscreen; cannot end the session or bypass enforcement
    ValueTask<UiNode> RenderAsync(UiRenderContext context, CancellationToken cancellationToken);
}
public sealed record UiRenderContext(SessionSnapshot Session, IUiSurface Surface);   // Surface.RequestRender() asks for a new render
public sealed record UiAction(string ElementId, UiActionKind Kind, string? Value, IReadOnlyDictionary<string, string> FormValues);
// UiActionKind: Click | Toggle (Value "true"/"false") | Submit (FormValues). Test helpers: UiAction.Click / Toggle / Submit.
```

If two interfaces of one class have the same `RenderAsync` signature (settings and block screen), implement them in
separate classes (as the template does) or explicitly.

### Events and the capability each needs

| Event | Capability | Fields |
|---|---|---|
| `SessionStarted` | `events.session` | `SessionId`, `PlannedDuration`, `OccurredAt` |
| `SessionEnded` | `events.session` | `SessionId`, `Reason` (`Expired`, `BreakGlass`, `DeveloperAbort`), `OccurredAt` |
| `EmergencyPassStarted` | `events.session` | `SessionId`, `Duration`, `PassesLeftToday`, `OccurredAt` |
| `EmergencyPassEnded` | `events.session` | `SessionId`, `OccurredAt` |
| `ProcessBlocked` | `events.process` | `Process`, `Reason` (`NotAllowlisted`, `BypassTool`, `PluginPolicy`), `BlockedByPlugin`, `OccurredAt` |
| `ActiveWindowChanged` | `events.window` | `Window` (`Title` null without `events.window.title`), `OccurredAt` |
| `UserIdleChanged` | `events.idle` | `IsIdle`, `IdleDuration`, `OccurredAt` |
| `CustomPluginEvent` | `events.custom` to publish | `Topic` (`{pluginId}/{eventName}`), `SourcePluginId`, `PayloadJson`, `OccurredAt` |

## 5. UI

Plugins build UI only from declarative blocks in `Blocker.UI.Sdk` through the `Ui` factory (`Ui.Card`, `Ui.Stat`,
`Ui.Progress`, `Ui.Button`, `Ui.Toggle`, `Ui.Form`, `Ui.Text`, `Ui.Row`, `Ui.Column`, `Ui.Grid`, `Ui.BarChart`, ...).
The host renders them with its own style. Read `STYLE_GUIDE.md` for the catalogue, limits and copywriting.
Element ids (buttons, toggles, forms and form fields) must be unique within one tree: `^[A-Za-z][A-Za-z0-9_-]{0,63}$`.
Validate every `UiAction` (kind, element id, value, form values) before acting; ignore anything unexpected.

## 6. Hard rules

* Time: use `context.Time` (`GetUtcNow()`, `GetTimestamp()`). Never `DateTime.Now` or `DateTime.UtcNow`.
* Forbidden APIs (static analysis rejects the plugin): `System.Diagnostics.Process`, `Microsoft.Win32` (Registry), `System.IO.File`,
  `Directory`, `FileStream`, `Path` for file access (use `context.Storage`), P/Invoke (`DllImport`, `LibraryImport`), `unsafe` code,
  `System.Reflection`, `Reflection.Emit`, `Assembly.Load`, `AppDomain`, `Environment` access to the machine, `Console`.
* Threads: no `Thread`, no `Task.Run` loops, no blocking (`.Wait()`, `.Result`, `Thread.Sleep`, `GetAwaiter().GetResult()`).
  Use `async`/`await`, honour `CancellationToken`, and use `ITickListener` for periodic work.
* `async void` is forbidden. Event handlers and callbacks return `ValueTask`/`Task`.
* Network only with `network` capability, never in the tests.
* Plugins can only **tighten** enforcement. There is no allow verdict: an interceptor can `Block` or `Abstain`. Critical Windows
  processes are never blocked regardless.
* Lock votes may only veto voluntary actions (uninstall, remove/disable plugin, change protection settings, emergency pass).
  They can never extend a session; the natural session end cannot be prevented. A faulted voter counts as `Allow`.
* Window titles are `null` unless the plugin has `events.window.title`. Do not log or store them.
* IPC payloads and custom events are untrusted JSON of at most 64 KiB: parse defensively, validate every field, never trust sizes.
* Never throw from callbacks for expected conditions; return a neutral result instead.
* Storage: keys follow the key rules, values are strings, respect `QuotaBytes`. Persist small state, not logs.
* Do not edit `plugin.json` capabilities to "make it compile": add a capability only when the feature really needs it.

## 7. Tests

* `PluginTests.cs` is mandatory. Blocker compiles it with your code and runs every test against `FakePluginContext`
  before installing. **All tests must pass; any failure blocks installation.**
* Allowed: xUnit v3 `[Fact]`, `[Theory]` with `[InlineData]`, and `Xunit.Assert`. Not allowed: `Skip`, `MemberData`/`ClassData`,
  fixtures, `async void`, real time (`Task.Delay`, `DateTime.Now`), file or network access.
* Test classes are `public`, have a public parameterless constructor and live in namespace `<YourNamespace>.Tests`.
* Use `Blocker.Plugin.Testing`:
  `new FakePluginContext(pluginId, capabilities)` (no capabilities means all granted), `FakeTime`, `FakeSession.Current`,
  `Storage`, `Logger.Entries`, `RegisteredExtensions`, `GetExtension<T>()`, `PublishAsync(evt, ct)`, `InvokeIpcAsync(method, json, ct)`,
  `TickAsync(advance, ct)`, `RenderAsync(contribution, ct)` (validates the tree like the host), `SendActionAsync(contribution, action, ct)`,
  `RenderRequests`; and `PluginTestData.Process(...)`, `PluginTestData.ActiveSession(remaining, passesLeft)`.
* Assert exact values (`Assert.Equal("3", ...)`), and cover negative paths: missing capability, invalid input, hidden UI, idle session.
* Optional local run, only when a .NET 10 SDK is installed: create a test project that references the Blocker SDK, UI SDK and testing kit,
  link these `.cs` files and run `dotnet test`. Not needed: Blocker runs the same tests on upload.

## 8. Packaging

1. Make sure `plugin.json` is valid and the version is higher than the installed one.
2. Select the **contents** of this folder (not the folder itself) and zip them, so `plugin.json` is at the ZIP root.
3. The ZIP contains only `.cs`, `.json`, `.md` files, at most 50 files and 2 MB unpacked.
4. In Blocker open Plugins, choose the ZIP. Installing, updating, removing or disabling plugins is blocked while a detox session is active.

## 9. Common errors and fixes

| Message / symptom | Fix |
|---|---|
| `plugin.json` missing or not at root | Zip the folder contents, not the folder. |
| Invalid plugin id | Lowercase reverse-DNS, 2 to 5 segments, e.g. `com.example.my-plugin`. |
| Unknown capability | Use only names from the capability table. |
| Entry type not found / no parameterless constructor | `entryType` must be `Namespace.Class`, public, implementing `IBlockerPlugin`. |
| Uses API X which requires capability Y | Add Y to `capabilities`, or remove the API call. |
| Forbidden API | Replace by SDK services (storage instead of files, `context.Time` instead of `DateTime.Now`). |
| `PluginCapabilityException` in a test | The test context was created without that capability, or the manifest list is incomplete. |
| `UiValidationException` | Fix the tree: unique ids, texts under 2000 chars, no control characters, progress value between 0 and max. |
| Plugin `Faulted` | A callback exceeded 50 ms or threw repeatedly: render from cache, move work to a tick listener. |
| Test passes locally but fails on upload | Tests must not depend on order, time or machine state; use fakes only. |

## 10. Definition of done

- [ ] `plugin.json`: unique `id`, valid `version`, `sdkVersion` `1.0`, correct `entryType`, minimal capabilities that match the code.
- [ ] No forbidden API, no `DateTime.Now`, no `async void`, no blocking calls, no empty `catch`.
- [ ] Every callback runs well under 50 ms (renders use cached state only).
- [ ] All user and IPC input is validated; unexpected input is ignored.
- [ ] UI follows `STYLE_GUIDE.md` (declarative blocks only, semantic tones, plain text, valid tree).
- [ ] `PluginTests.cs` covers each feature and at least one negative case per feature; exact assertions; no `Skip`.
- [ ] ZIP contains only `.cs`, `.json`, `.md` files with `plugin.json` at the root.
