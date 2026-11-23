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

## Rollback journal
Write-ahead: record intent + original state (DPAPI LocalMachine + HMAC-SHA256) → flush → apply → mark applied. Rollback replays in reverse, idempotent. Every artifact we create carries a `BlockerOwned=<installId>` marker so a sweep removes it even if the journal is damaged. Covers IFEO, service DACL, NTFS deny ACEs, browser policies. Runs at session end, at boot after expiry, on recovery and uninstall.

Required vs best-effort: the planner marks IFEO redirects, NTFS deny ACEs and service DACLs `BestEffort`. If Windows refuses one at session start (access denied, IO, Win32, security), the journal undoes that entry, the coordinator logs a warning, continues and reports the skipped targets (`StartEnforcedSessionResult.Warnings`, a `ProtectionDegraded` notification, a snackbar in the UI); the ETW monitor and reconcile still kill the programs. They are not retried within the session. Browser policies, process monitor, proxy and firewall stay fatal and roll the whole session back.

## Time
Elapsed time: `TickCount64` checkpoints. Anchor: NTP consensus + HTTPS `Date` header. Offline reboot ⇒ powered-off time does not count. Time zone frozen at session start for the daily emergency-pass reset.

## Plugin pipeline
ZIP validation (Zip Slip, ≤ 2 MB unpacked, ≤ 50 files, `.cs/.json/.md` only, `plugin.json` schema) → Roslyn compile against `Basic.Reference.Assemblies.Net100` + SDK refs → semantic API-policy analysis vs. capabilities (allows `System.Threading.Interlocked`/`Volatile`) → `PluginTestRunner` on `FakePluginContext` → install. Tests use the xUnit v3 attribute subset (`Fact`, `Theory`, `InlineData`); Skip/Explicit fail.

## Repository conventions
CPM, `ArtifactsPath`, warnings as errors, PublicAPI tracking (`eng/update-public-api.sh`), xUnit v3 on MTP, Windows-only tests tagged `Category=WindowsOnly`, slopwatch gate (`.slopwatch/`), CI on ubuntu + windows.
