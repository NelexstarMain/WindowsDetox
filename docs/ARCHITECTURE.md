# WindowsDetox — architecture decisions

Approved 2026-11-17. Target: Windows 10/11, .NET 10 LTS, self-contained + ReadyToRun (NativeAOT forbidden).

## Threat model (summary)
- Goal: stop *impulsive* bypass. A determined administrator (SeDebug/SeTakeOwnership, Safe Mode, boot media) can always win without a signed kernel driver. Recommend daily use as a standard user.
- Trust boundaries: named pipe (UI/Agent/Stub/PluginHost → Service), plugin ZIP + compiled code, plugin UI trees, plugin IPC payloads, network time sources.

## Processes
| Process | Identity | Role |
|---|---|---|
| Blocker.Service | SYSTEM, Session 0 | Sole decision maker: session clock, allowlist engine (ETW + IFEO), WFP dynamic session, local CONNECT/SNI proxy, rollback journal, pipe server. `--console`, `--dev-mode` (DEVMODE builds only). |
| Blocker.Watchdog | SYSTEM service | Mutual supervision with Service (process-handle waits + SCM restart actions), < 1 s restart. |
| Blocker.PluginHost | restricted token (low IL / AppContainer) | Roslyn compile, API-policy analysis, test run, plugin execution. Killable → hard circuit breaker. |
| Blocker.UI (`--agent` mode too) | user | WPF-UI shell. Agent mode: foreground window, idle, toasts, graceful WM_CLOSE. Never decides. |
| Blocker.Stub | user | Block screen (renders plugin UiNode trees). |

## Key decisions
1. **Plugins out of process** (PluginHost). UI contributions are serializable `UiNode` trees rendered by the host → style sandbox by construction. Plugin install requires an Administrator caller; blocked during sessions.
2. **Break-glass code**: one-time recovery code generated at session start (shown once, stored only as a salted hash). Ends the session with full rollback (`SessionEndReason.BreakGlass`).
3. **.NET 10 LTS** (.NET 8 support ended 2026-11-10).
4. **Web filtering without a kernel driver**: browser policies (HKLM, journaled) set PAC `PROXY 127.0.0.1:port; DIRECT`, disable DoH and QUIC; WFP dynamic session (`FWPM_SESSION_FLAG_DYNAMIC`) blocks browser direct TCP 80/443, UDP 443 and 853. Service death ⇒ WFP filters vanish ⇒ PAC falls back to DIRECT ⇒ network normal. No hosts-file edits.

## Enforcement rules (in order)
Session 0 → never touched · hardcoded critical set → never killed · bypass tools → always blocked · user allowlist (SHA-256 / path+hash / PFN) · inheritance only from user-allowlisted parents (PID + start time) · Microsoft-signed system binaries baseline · everything else → terminate + Stub. Plugins may only tighten (no "allow" verdict); lock votes can never extend a session; faulted contributors count as Allow.

**Per-app rules (outside sessions).** A running session always decides first and these rules never apply during it (an emergency pass counts as a session). Without a session, an app with a rule is terminated and replaced by the block screen (`AppScheduledBlock` / `AppBudgetExhausted`) when it is inside a scheduled weekly block, otherwise when its usage-time pool (daily, per weekday or weekly) is used up. The same guards as in a session apply: Session 0, the critical set (including the UAC prompt) and Blocker's own processes are never touched; neither are programs in the Windows directory (outside its user-writable subtrees such as `Tasks` and `Temp`), and a rule for one is refused. Apps are identified like allowlist entries (hash, then path for entries without a hash, then package family name). Matching goes by path and package first; an image is hashed only when a hash rule is left to decide, never from a share or device path, not above 512 MB, and for at most 2 s (a timed-out image is not retried for a minute). Usage is the foreground time reported by the agent (only the connection that registered through `agent.register` is believed, the foreground image is read from the system by pid and start time instead of taken from the payload, and a foreground change ends an idle state because the agent sends no heartbeat), counted once a second only while the agent is connected and the user is not idle (a single step is capped at 5 s), also during sessions, and written every 30 s and on shutdown. The ETW monitor is shared with the session pipeline and started lazily once any rule can block; a reconcile pass every 5 s (and at once when a pool runs out) terminates running instances the events missed. Wall-clock ranges and day/week boundaries use the time zone captured when the rules were saved (kept while an app is blocked) and verified time. While an app is blocked its rule can only be replaced by an identical or stricter one, cannot be removed, and its identity can only be widened (the new entry keeps every path, hash and package key of the old one). A process that could not be terminated is retried by the next reconcile pass up to three times before the block screen is shown. A usage day or week more than a day ahead of the clock is reset, so a clock glitch cannot lock a pool. Nothing on the system is changed, so there is nothing to journal or roll back.

## Rollback journal
Write-ahead: record intent + original state (DPAPI LocalMachine + HMAC-SHA256) → flush → apply → mark applied. Rollback replays in reverse, idempotent. Every artifact we create carries a `BlockerOwned=<installId>` marker so a sweep removes it even if the journal is damaged. Covers IFEO, service DACL, NTFS deny ACEs, browser policies. Runs at session end, at boot after expiry, on recovery and uninstall.

Required vs best-effort: the planner marks IFEO redirects, NTFS deny ACEs and service DACLs `BestEffort`. If Windows refuses one at session start (access denied, IO, Win32, security), the journal undoes that entry, the coordinator logs a warning, continues and reports the skipped targets (`StartEnforcedSessionResult.Warnings`, a `ProtectionDegraded` notification, a snackbar in the UI); the ETW monitor and reconcile still kill the programs. They are not retried within the session. Browser policies, process monitor, proxy and firewall stay fatal and roll the whole session back.

## Time
Elapsed time: `TickCount64` checkpoints. Anchor: NTP consensus + HTTPS `Date` header. Offline reboot ⇒ powered-off time does not count. Time zone frozen at session start for the daily emergency-pass reset, and when per-app rules are saved for their schedules and usage periods.

## Plugin pipeline
ZIP validation (Zip Slip, ≤ 2 MB unpacked, ≤ 50 files, `.cs/.json/.md` only, `plugin.json` schema) → Roslyn compile against `Basic.Reference.Assemblies.Net100` + SDK refs → semantic API-policy analysis vs. capabilities (allows `System.Threading.Interlocked`/`Volatile`) → `PluginTestRunner` on `FakePluginContext` → install. Tests use the xUnit v3 attribute subset (`Fact`, `Theory`, `InlineData`); Skip/Explicit fail.

## Repository conventions
CPM, `ArtifactsPath`, warnings as errors, PublicAPI tracking (`eng/update-public-api.sh`), xUnit v3 on MTP, Windows-only tests tagged `Category=WindowsOnly`, slopwatch gate (`.slopwatch/`), CI on ubuntu + windows.
