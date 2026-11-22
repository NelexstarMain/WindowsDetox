# Data storage audit — 2026-11-22

Scope: every persistent store. WindowsDetox has no SQL database; "the database" is the set of files under `ProgramData`:
sealed service state (`SecureFileStore`, DPAPI + HMAC-SHA256), the Rollback Journal, the per-plugin `IPluginStorage`, the bundled
plugins' documents (Goals, Insights, Calendar) and the UI widget layout.

Method (per the project spec): the main thread audited the crypto core and the journal; two read-only Sonnet subagents reviewed
plugin storage and plugin business stores in parallel. Every high/medium item below was re-checked in the source by the main thread.
Static review only — Windows-only behavior (DPAPI, ACLs, SCM) was not executed.

## Core: sealed store and Rollback Journal

| # | Sev | Location | Defect | Failure scenario |
|---|-----|----------|--------|------------------|
| C1 | **High** | `src/Blocker.Service/WindowsServicePorts.cs:115`, `ServiceDaclChangeHandler.cs:26` | `ServiceDaclChangeHandler` is built without default SDDLs, so its marker sweep is a no-op. | Journal fails verification (see C2) → `RollbackAllAsync` falls back to the sweep → the restricted service DACL stays; admins can no longer stop or uninstall the service. Breaks the reversibility guarantee. |
| C2 | **High** | `src/Blocker.Core/Security/HmacKeyStore.cs:46-55` | Any `CryptographicException` from DPAPI (or a wrong-sized key) silently creates and **overwrites** `hmac.key`. | A transient DPAPI failure rotates the key → journal, session and policy files all read as tampered → session cleared and journal replaced by the (incomplete, see C1) marker sweep. Safer: keep the old file (`hmac.key.bad`) and fail closed instead of overwriting. |
| C3 | Medium | `src/Blocker.Core/ServiceHost/BlockerServiceApp.Runtime.cs:305-313` | `InstallIdStore` regenerates the id when `install.id` is missing or damaged. | All `BlockerOwned=<oldId>` markers (IFEO, policies) become invisible to the sweep; together with a damaged journal nothing is cleaned up. |
| C4 | Low | `src/Blocker.Core/Security/AtomicFile.cs:15-30` | `File.Move` is not write-through and the directory entry is not flushed; crash leftovers `*.tmp` are never cleaned. | Power loss right after a move can resurrect the previous version; stray `.tmp` files accumulate in `ProgramData`. |
| C5 | Low | `src/Blocker.Core/Journal/RollbackJournal.cs:175` | `JournalDocument.Version` is never checked; a null `Entries` would throw `NullReferenceException` outside the damaged-journal path. | Only reachable with a validly MAC'd but foreign document; defensive gap. |
| C6 | Low | `src/Blocker.Watchdog/Program.cs:32`, `BlockerServiceApp.cs:68,213` | Several independent `HmacKeyStore` instances (service ×2, watchdog) lazily create the same key file. | On a first run with no key, service and watchdog can each generate a key; the loser keeps writing with a key that no longer exists on disk. |

Checked and fine: envelope verifies the MAC in constant time before unprotecting, length/magic/version validated; journal is
write-ahead (Pending persisted before apply), keeps the first original per target, undoes a failed apply, and bring-up rolls back
before applying so the journal does not grow across sessions; damaged supervision file fails safe (keep supervising).

## Plugin storage layer (`IPluginStorage`)

| # | Sev | Location | Defect | Failure scenario |
|---|-----|----------|--------|------------------|
| P1 | Medium | `src/Blocker.Core/PluginRuntime/FileBackedPluginStorage.cs:178` | A damaged `storage.json` is treated as empty; the next write replaces it. No backup or quarantine. | One corrupt byte permanently erases all data of a plugin. |
| P2 | Medium | `src/Blocker.Platform.Windows/Changes/PluginDataDirectory.cs:31` | One inherited DACL (Users: modify, Low IL) covers the whole plugin data root; isolation is only path logic in one host process. | A plugin using `System.IO` (or any user process) can read/overwrite another plugin's `storage.json`. Needs a decision: accept as documented trust model or per-plugin ACLs. |
| P3 | Medium | `src/Blocker.Core/PluginRuntime/PluginStore.cs:150` | `OpenStorage` returns a new instance each call; cache, gate and quota counter are per instance. | Two live instances for one plugin (reload overlap) overwrite each other's whole-file writes and bypass the quota. |
| P4 | Medium | `FileBackedPluginStorage.cs:148` | Only write-path `IOException`/`UnauthorizedAccessException` are wrapped (inner exception dropped); read path leaks raw `IOException` with host paths; lone surrogates throw a non-contract exception the in-memory fake accepts. | Plugin sees undocumented exceptions; plugin tests pass against the fake but fail in production. |
| P5 | Medium | `FileBackedPluginStorage.cs` quota calc | Quota counts `chars × 2`, but the default JSON encoder escapes non-ASCII and `&<>'+` as `\uXXXX`. | File on disk can be ~3× the quota. |
| P6 | Low | `src/Blocker.SDK/Storage/IPluginStorage.cs:69`, `src/Blocker.SDK/PluginId.cs` | Regexes end with `$`, which matches before a trailing `\n`; should be `\z`. | `"abc\n"` is accepted as a key / plugin id, contradicting the documented character set. |
| P7 | Low | `PluginStore.cs:99-107` | `Remove` deletes the package before the data; failure leaves orphaned data that a reinstall of the same id inherits; IPC returns `ex.Message` with host paths. | Stale data reappears after reinstall. |

## Bundled plugin stores and UI layout

| # | Sev | Location | Defect | Failure scenario |
|---|-----|----------|--------|------------------|
| B1 | **High** | `plugins/GoalsTasks/GoalsStore.cs:74`, `plugins/FocusInsights/InsightsStore.cs`, `plugins/Calendar/CalendarStore.cs` | `Version` is written but never compared; `Sanitize` rebuilds as v1 and drops unknown fields/enum values (Calendar `continue`s unknown `Kind/Repeat/End`). | Downgrade after a newer build: first edit permanently deletes the newer data (e.g. every monthly series). |
| B2 | **High** | `GoalsStore.cs:146-150`, `InsightsStore.cs:232-236` | Unreadable `data` is only logged, replaced with an empty document and overwritten on the next change (Calendar keeps a copy, these two do not). | A truncated write wipes 7 days of tasks / 90 days of insights. |
| B3 | Medium | `GoalsStore.cs:320,370`, `InsightsStore.cs:464-471,536` | Retention pruning uses the current clock with no sanity check. | Clock jumps far ahead (CMOS reset) → all history deleted on the next event. |
| B4 | Medium | `GoalsStore.cs:336`, `InsightsStore.cs:500` | `_dirty` is cleared before the write and only `PluginStorageException` is caught. | A transient `IOException` escapes the event handler and the change is never retried or flushed. |
| B5 | Medium | `InsightsStore.cs:307-331` | Blocked attempts are written only on a later event after 10 s or at shutdown. | Crash loses every attempt since the last write; an isolated attempt stays unsaved indefinitely. |
| B6 | Medium | `CalendarStore.cs:364-376` | `RecordAsync` returns true even when the history write failed. | With storage full, an auto-started occurrence can start again after a restart (breaks "never started twice"). |
| B7 | Medium | `CalendarStore.cs:557-560` | The kept unreadable copy (≤200k chars) is never deleted and counts against the 1 MiB quota. | After one corruption later writes can hit the quota and stay memory-only. |
| B8 | Low | `src/Blocker.UI/Services/WidgetLayoutStore.cs:78-97` | `WriteAllText` + `Move` without flush, fixed temp name; one bad element nulls the whole layout, next commit overwrites it. | Layout silently resets to defaults after power loss or an upgrade. |
| B9 | Low | Goals/Insights/Calendar (various) | `SetDefaultGoalAsync`/`SetRecordNamesAsync` bypass the write gate; ±1 goal read-modify-write across calls; `_nextId` restarts at 1 after corruption (collides with history keys); id counters unchecked for overflow; pending session overwritten on missed end event; day numbers stored without zone. | Lost clicks, a recurring entry that never auto-starts, uncounted sessions. |

## Test gaps
- No test for: concurrent writes / quota under concurrency, two storage instances on one file, read-path `IOException`,
  crash between temp write and move, `Remove` failing, reinstall data policy, newer `Version` or unknown enum values,
  clock jumps for Goals/Insights, zero-byte `widgets.json`, `PluginDataDirectory.Provision/Remove` (SDDL only string-matched),
  `HmacKeyStore` overwrite-on-unprotect-failure, DACL sweep without defaults.

## Recommended order
1. C1 + C2 (reversibility): capture default service SDDLs at install and pass them to the handler (or reset to the known
   SCM default); stop overwriting `hmac.key` on unprotect failure.
2. B1 + B2 + P1 (silent data loss): refuse to overwrite documents with a newer `Version`/unknown values (read-only mode),
   quarantine unreadable documents instead of discarding.
3. B3–B6, P3–P5: retry/flush semantics, clock sanity, single storage instance per plugin, quota on encoded bytes.
4. Low items and the test gaps above.

## Baseline
`dotnet build -c Release`: 0 warnings, 0 errors. `dotnet test --filter-not-trait Category=WindowsOnly`: 1417/1417 passed (Linux container, 2026-11-22).
