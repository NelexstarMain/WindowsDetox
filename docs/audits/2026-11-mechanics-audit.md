# Mechanics audit — 2026-11-23

Scope: the enforcement mechanics of `src/Blocker.Core`, `Blocker.Service`, `Blocker.Platform.Windows`, `Blocker.Contracts`, `Blocker.Setup`
and the runtime/storage parts of `Blocker.SDK`. Out of scope: `src/Blocker.UI*`, `plugins/**`, and the data-storage findings of
`2026-11-data-storage-audit.md` (not repeated; where that audit's items are already fixed in the code, they are not re-listed).

Method: static review of the real code paths, cross-checked against the existing tests. Windows-only behavior (ETW, WFP, DACL, SCM, DPAPI,
installer) was **not executed**; every finding that depends on a Windows default is marked `verify on VM` with the exact check.
Baseline: `dotnet test --solution WindowsDetox.slnx -c Release --filter-not-trait "Category=WindowsOnly"` = 1493 passed, 0 failed, 0 skipped.

Threat model reminder (ARCHITECTURE.md): a determined *administrator* can always win; the target is the impulsive standard user. Findings below
are ranked by what a **non-admin** local user can do, except M1 (which is an elevation of privilege) and the installer items.

## Summary

| Severity | Count | Ids |
|---|---|---|
| Critical | 1 | M1 |
| High | 4 | M2, M3, M4, M5 |
| Medium | 10 | M6 – M15 |
| Low | 5 | M16 – M20 |

Themes:
1. **Trust in the file system without an ACL.** Neither the installer nor the service gives `ProgramData\WindowsDetox` an explicit DACL, the HMAC key is
   DPAPI *LocalMachine* with a constant public entropy, and the rollback journal is replayed as SYSTEM. Users can read the key and create files
   in the folder, so "tamper-evident" files are forgeable whenever they do not exist yet (M1, M16).
2. **The process pipeline has no second chance.** One ETW event, one queue, one consumer, no reconciliation, no health check (M3, M4, M5, M6). Every
   missed or delayed event is a permanent bypass of that process.
3. **Name/path based trust.** Critical-process and bypass-tool classification trusts file names under a directory prefix and a hash set that misses
   the common locations (M2, M8).
4. **Fail-open on failure paths without retry** (M7, M14).

Items marked **(Windows)** were reasoned from documented Windows behavior and should be confirmed on the test VM (`docs/VM_TESTING.md`).

---

## Findings

### M1 — Critical — Forged rollback journal executes arbitrary service-DACL changes as SYSTEM (standard user to SYSTEM)

- **Status:** Fixed in 9509947.
- **Where:** `src/Blocker.Platform.Windows/Changes/ServiceDaclChangeHandler.cs:85` (`RestoreAsync` writes the journal's `Sddl` for any service name),
  `ChangeGuard.cs:40` (`RequireServiceName` only checks syntax), `src/Blocker.Core/Journal/RollbackJournal.cs:111,243` (journal file is deleted after every
  successful rollback, so it does not exist while idle), `src/Blocker.Platform.Windows/Processes/DpapiSecretProtector.cs:15,23` (LocalMachine scope, constant
  entropy `"WindowsDetox.v1"`), `src/Blocker.Core/ServiceHost/BlockerServiceApp.cs:206-209` and `src/Blocker.Service/ServiceCommands.cs:106-123` (no DACL is ever
  applied to `DataRoot`; only `PluginDataDirectory` gets one).
- **Scenario:** `ProgramData` grants `Users` read and "create files/folders" on subfolders; a SYSTEM-created `WindowsDetox` folder inherits that. A standard user:
  1. reads `hmac.key`, calls `ProtectedData.Unprotect(..., "WindowsDetox.v1", LocalMachine)` (any local account can) and obtains the HMAC key;
  2. while idle (`journal.bin` absent), creates `journal.bin` as a valid `SecureEnvelope` containing one `Applied` entry
     `{Kind:"servicedacl", Target:"Spooler", OriginalStateJson:{"Sddl":"D:(A;;GA;;;WD)"}}`;
  3. starts a session from the UI (or waits for a service restart). `BringUpAsync` first runs `journal.RollbackAllAsync` (`EnforcementCoordinator.cs:372`) which
     replays the entry as SYSTEM: `TrySetSddl("Spooler", "D:(A;;GA;;;WD)")` — Everyone gets full control of a SYSTEM service; the user then reconfigures
     `binPath` and gets SYSTEM. `IsSafe()` exists but is only applied to `Apply`, not to `Restore`.
  The other handlers are properly guarded (`ntfsdeny` restore needs our deny ACEs present on a configured root, `ifeo` and `regpolicy` need our marker and
  a root allowlist); only `servicedacl` is open.
- **Verify on VM:** `icacls "%ProgramData%\WindowsDetox"` and `icacls ...\hmac.key` as a standard user — expect `BUILTIN\Users:(RX)` inherited and
  `BUILTIN\Users:(CI)(WD,AD)`. If the folder is already locked down by something not in this repo, drop to High.
- **Fix:**
  1. Provision `DataRoot` at `--install` with a protected DACL (SYSTEM + Administrators full; no access for Users), like `PluginDataSecurity`. Keep `hmac.key`,
     `*.bin`, `journal.bin` unreadable to Users. Re-apply on service start (idempotent, cheap).
  2. Make `Restore` defensive: `servicedacl` handlers must accept only the two Blocker service names (inject the allowed set, like `ntfsdeny` roots) and restore
     only an SDDL that parses and does not grant `WD`/`WO`/`DC`/`SD` to anyone but SYSTEM/Administrators (or restore only the install-time captured default).
  3. Do not rely on the HMAC key for secrecy: add per-install entropy kept in an ACL'd file, or switch to DPAPI `CurrentUser` of a dedicated service account.
- **Tests:** `ServiceDaclChangeHandlerTests`: `Restore_rejects_a_service_that_is_not_ours`, `Restore_rejects_an_sddl_that_grants_write_dac_to_everyone`.
  `RollbackJournalTests`: a sealed journal with an unknown/forbidden target ends as a failed entry, not as an executed change. Windows-only
  `DataRootSecurityTests`: after `Provision`, `Users` has no access (SDDL string and an actual `File.ReadAllBytes` as a restricted token).

### M2 — High — A copy of any program placed in a user-writable folder under `%SystemRoot%` named like a critical process is never blocked or killed

- **Status:** Fixed in 8ca0f10.
- **Where:** `src/Blocker.Core/Policy/CriticalProcesses.cs:66-91` (name in `SystemNames` plus *any* path under the Windows directory), used by
  `EnforcementPolicy.cs:230-233` (critical is decided *before* the bypass-tool and allowlist checks), `EnforcementCoordinator.Processes.cs:186-187`
  (`trusted` skips hashing) and `Win32ProcessController.cs:246` (the "second line of defence" calls the same function).
- **Scenario:** Authenticated Users can create and execute files in several subtrees of `C:\Windows` (**Windows**: `Temp`, `Tasks`, `tracing`,
  `Registration\CRMLog`, `System32\spool\drivers\color`, `debug\WIA` — the classic AppLocker-bypass folders). The user copies a game, `powershell.exe` or
  `cmd.exe` to `C:\Windows\Tasks\conhost.exe` (or `dllhost.exe`, `svchost.exe`, `explorer.exe`...) and runs it: `Classify` returns `System`, the verdict is
  `Allow(Critical)`, `TryTerminate` refuses it, and bypass-tool name/hash matching never runs. Tools that are on the bypass list work this way too.
- **Fix:** classify a critical name only at its canonical locations: a table `name → allowed relative directories` (`System32`, `SysWOW64`,
  `%SystemRoot%\explorer.exe`, `SystemApps\<pkg>`, `ImmersiveControlPanel`, `Microsoft.NET`...), and in addition reject any path below the known writable
  subtrees. As defence in depth, require `IsMicrosoftSigned` (cached per path/mtime/length, already implemented) for critical images outside session 0 and treat
  a *negative* verdict as "not critical" only when the file is below a writable subtree (so a signature-service hiccup never kills `explorer.exe`).
- **Tests:** `CriticalAndBypassTests`: `Classify(@"C:\Windows\Tasks\svchost.exe") == None`, same for `Temp\explorer.exe`, `System32\spool\drivers\color\x\dllhost.exe`;
  positives `System32\svchost.exe`, `SysWOW64\svchost.exe`, `explorer.exe`, `SystemApps\...\SearchHost.exe`. `EnforcementPolicyTests`: a bypass-tool hash in a fake
  `C:\Windows\Tasks\conhost.exe` is blocked.

### M3 — High — Parent-process spoofing turns any blocked launcher into an "allowlisted parent"

- **Status:** Fixed in ebc7149.
- **Where:** `src/Blocker.Platform.Windows/Processes/EtwProcessMonitor.cs:151-156` (uses the payload `ParentProcessID`), `src/Blocker.Core/Policy/ProcessTree.cs:435-441`
  (`ResolveParentLocked`), `EnforcementPolicy.cs:258-261` (`InheritedFromAllowed`). `ProcessInfo` has no creator field; `EnumerateRunning` uses
  `th32ParentProcessID`, which is equally spoofable.
- **Scenario:** a small unlisted launcher runs for the few milliseconds before its own verdict kills it (`WaitToRead` → hash → decide → terminate; see M5, the
  window is arbitrarily long). It calls `CreateProcess` with `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` = the pid of any running allowlisted process (same user and
  integrity level are enough). The ETW payload says "parent = allowlisted app"; the child (game, browser copy, `powershell` copy) is `InheritedFromAllowed`
  and survives. No admin needed.
- **Fix:** record the *real creator*: for `Microsoft-Windows-Kernel-Process` event 1 the event header process id (`data.ProcessID`) is the creating process, the
  payload `ParentProcessID` is the (possibly spoofed) inherited parent (**Windows**, verify with a spoofing test binary). Add `CreatorProcessId` to `ProcessInfo`; inherit
  only when creator == parent (or when creator itself is an allowed node); log and treat a mismatch as "no inheritance". For the reconcile path (M4) keep the
  `StartTime` guard and additionally require the parent to still be alive.
- **Tests:** `ProcessTreeTests`/`CoordinatorProcessTests`: child event with `Creator != Parent` where `Parent` is allowlisted is blocked. Windows-only
  `EtwProcessMonitorTests`: spoofed-parent child reports a differing creator.

### M4 — High — No reconciliation and no health check: one lost ETW event is a permanent bypass of that process

- **Status:** Fixed in de2e7cf (periodic reconcile every 5 s through `TimeProvider`, monitor restart on `Completion`/`Fault`, `ProtectionDegraded` while it stays down). Not done: the lock-free ETW callback queue from fix item (3); the reconcile covers a dropped event, but a full queue can still block the ETW thread. Verify on VM: kill the `Blocker-ProcessMonitor` trace session during a session; it is restarted within about 5 s and a process started meanwhile is blocked.
- **Where:** `EtwProcessMonitor.cs:129-139` (loop death only sets `Fault`; `Completion`/`Fault` have no consumer in the repo), `EnforcementCoordinator.Processes.cs:62-84`
  (the monitor thread is blocked with `GetResult()` when the 4096-item queue is full — the kernel then drops events anyway, so "never drop" is not true),
  `EnforcementCoordinator.cs:343,519` (`EnforceRunningAsync` runs only at bring-up and after a pass).
- **Scenario:** a process-creation flood (or M5's stalled consumer) overruns the real-time ETW buffers, or the ETW session is stopped/crashes. New processes are
  never reported; nothing re-enumerates, nothing notices. A process that slips through stays allowed for the whole session (up to 30 days).
- **Fix:** (1) in the tick loop run a reconcile every 2-5 s: `monitor.EnumerateRunning()`, decide every pid/start-time not in `runtime.Tree` (cheap: the tree
  lookup is O(1), `DecideAsync(force:false)` already skips known nodes); (2) expose `IProcessMonitor.Health` (`Faulted`, `EventsLost`, `Completion`), restart the ETW session
  on fault and call `EnforceRunningAsync` right after; (3) never block the ETW callback: use a lock-free bounded queue and count overflows, and let the reconcile
  (1) cover them; (4) surface `ProtectionDegraded` when the monitor is down.
- **Tests:** `CoordinatorProcessTests` with a fake monitor that drops `ProcessStarted`: the process is blocked within N ticks. Fake monitor `Completion` completes:
  coordinator restarts it and enforces. Queue-full test: the monitor callback returns within a budget.

### M5 — High — Head-of-line blocking in the single-consumer pipeline (hash, signature, plugin interceptor have no hard budget)

- **Status:** Deferred: the bounded, partitioned decision workers with a fail-closed budget restructure the single-consumer pipeline (ordering parent before child, `DecideAsync`, hashing) and need their own change and review. M4 reconciliation shortens the window of a stalled consumer but does not remove head-of-line blocking.
- **Where:** `EnforcementCoordinator.Processes.cs:86-114` (one consumer), `:185-194` (`HashAsync` of the whole image, `IsMicrosoftSigned`, `TryGetPackage`, interceptor up to
  `InterceptorBudget` 100 ms per allowed process), `FileHasher.cs:53-56` (no size or time limit, `FileShare.ReadWrite`).
- **Scenario:** the user first launches a process whose image is slow to read (multi-GB file, SMB share, removable disk, OneDrive/Files-On-Demand placeholder that triggers a
  download, antivirus scanning it). Every process started afterwards waits behind it; a renamed copy of a blocked app that starts in that window runs for minutes.
  Spawning many *allowed* processes (each costing an interceptor round trip when the plugin host is slow) has the same effect and also fills the queue (M4).
- **Fix:** decouple "verdict" from "expensive facts": decide cheap facts first (critical, path/package allowlist, bypass-name); for an image whose hash/signature is
  not cached run the expensive step in parallel workers (partitioned by pid so parent-before-child order is kept) with a hard budget (e.g. 1.5 s) and **fail closed**
  (block) when the budget is exceeded for a not-yet-allowed process; skip hashing above a size cap unless an allowlist hash entry exists. Cache negative results.
- **Tests:** `CoordinatorProcessTests` with a `FileHasher` fake that blocks for the first image: a second not-allowlisted process is still terminated within the budget;
  a hash timeout yields `Block`, not `Allow`.

### M6 — Medium — During the first (daylight-saving) pass of the repeated hour no ETW-reported process can be terminated

- **Status:** Fixed in 74ab216 (`ProcessStartTimeResolver`: `GetProcessTimes` key, payload only as fallback, exit reuses the start key). Verify on VM: set the clock into the repeated DST hour (for example 2026-10-25 02:30 first pass, Central European time) and block a process that the ETW monitor reports.
- **Where:** `EtwProcessMonitor.cs:14-17` (documented limitation), `:188-194` (`local.ToFileTime()`), `Win32ProcessController.cs:240` (`ticks != startTimeTicks` → `false`).
- **Scenario:** at the end of DST (for example the last Sunday of October in Poland) TraceEvent hands out `CreateTime` as a local `DateTime`; for the first occurrence of
  the repeated 01:00-02:00 hour `ToFileTime()` resolves to standard time, so the start-time key is one hour late. `TryTerminate` compares it to `GetProcessTimes` and refuses.
  For that hour every blocked process keeps running; the same wrong key also breaks `ProcessTree` identity versus `EnumerateRunning`. The remark understates the effect: it is not
  "off by one hour", it is "no enforcement for one hour per year", on a date everybody can look up.
- **Fix:** take the real start time at event time: open the pid (`PROCESS_QUERY_LIMITED_INFORMATION`) and use `GetProcessTimes`; fall back to the payload only if the open
  fails, and read the payload as raw FILETIME (`data.EventData()` offset) instead of the converted `DateTime`. Also use the corrected value for `ProcessExited`.
- **Tests:** windows-only `EtwProcessMonitorTests`: `ReadFileTime` with an ambiguous local time returns the exact FILETIME (use a custom `TimeZoneInfo` via an injected converter);
  `Win32ProcessControllerTests`: terminate a child using the monitor-reported key.

### M7 — Medium — If the rollback at session end fails once, nothing retries until the service restarts

- **Status:** Fixed in 4b79381 (background retry 10 s doubling to 5 min, cancelled by a new session or disposal, `EnforcementCoordinator.CleanupPending` and `SessionStatusDto.CleanupPending`; the UI does not show the flag yet). Verify on VM: hold an IFEO key open with a handle at session end and release it; the redirects disappear without a service restart.
- **Where:** `EnforcementCoordinator.cs:533-558` (`TeardownAsync`), `:591-609` (`RollbackAllGuardedAsync` only logs and notifies "Blocker retries when the service restarts"),
  retry exists only in `RecoverOnStartupAsync` → `RollbackLeftoversAsync` (`:611-631`); the session-time `ResumeAsync` retry (`:505-510`) is gone once the runtime is torn down.
- **Scenario:** at expiry an AV scanner holds a policy key or the install-directory ACL write fails transiently. The session is over (`session.bin` cleared) but IFEO redirects stay
  (every non-allowlisted installed app shows the block screen), the restricted service DACL stays (admins can no longer stop or uninstall the service), deny ACEs stay on the install
  directory (updates/uninstall fail with access denied) and the browsers keep the dead PAC. Only a reboot or service restart repairs it.
- **Fix:** after `TeardownAsync`, when `!report.Complete`, start a background "cleanup retry" task (10 s doubling to 5 min, cancelled on dispose or when a new session starts) that
  calls `RollbackAllAsync` until `HasPendingChangesAsync` is false; report it in the status DTO (`CleanupPending`) so the UI can show a warning.
- **Tests:** `CoordinatorSessionTests`: a journal fake that fails the first rollback — after a few fake-time ticks the second attempt runs without `RecoverOnStartupAsync`; a new session
  started meanwhile cancels the retry.

### M8 — Medium — Renamed copies of `powershell`, `regedit`, `wt`, `pwsh` are not hash-matched; name-only gaps (`wsl`, `mstsc`, `ssh`, `regsvr32`...)

- **Where:** `src/Blocker.Service/WindowsServicePorts.cs:132-155` (hashes only `System32\<name>.exe` and `SysWOW64\<name>.exe`), `BypassTools.cs:111-118`,
  `EnforcementPolicy.cs:263` (`SystemBaseline` allows every Microsoft-signed file in `System32`/`SysWOW64`/`SystemApps`).
- **Scenario A (hash gap):** `powershell.exe` lives in `System32\WindowsPowerShell\v1.0\`, `regedit.exe` in `%SystemRoot%`, `pwsh` in `Program Files\PowerShell\7`, `wt`/`WindowsTerminal`
  in `WindowsApps`. A copy renamed `notes.exe` has neither a listed name nor a listed hash. It is blocked as `NotAllowlisted`, **unless** its parent is an allowlisted app that can start a
  program of its choice (an IDE terminal profile, a build task, any scripting runtime): then it is `InheritedFromAllowed` and runs.
- **Scenario B (name gap):** Microsoft-signed `System32` programs that are complete alternative environments are allowed by the baseline: `wsl.exe`/`bash.exe` (Linux GUI apps never appear as Windows
  processes), `mstsc.exe` (remote desktop to another machine), `OpenSSH\ssh.exe`, `telnet`, `regsvr32`, `cmstp`, `forfiles`, `pcalua`, `odbcconf`.
- **Fix:** match on the PE version-info `OriginalFilename`/`InternalName` read from the image at decision time (survives renames and Windows updates, no hash list to go stale) in
  addition to names and hashes; hash recursively (`System32\**`, `%SystemRoot%\*.exe`, `Program Files\PowerShell`); extend the name list (`wsl`, `wslhost`, `bash`, `mstsc`, `ssh`, `telnet`,
  `regsvr32`, `cmstp`, `forfiles`, `pcalua`, `odbcconf`) and make the baseline opt-out per name. Keep the real binary's hash list refreshed at every session start, not only at service start.
- **Tests:** `CriticalAndBypassTests`: `IsBypassTool("C:\\Users\\u\\notes.exe", hash:null, originalFilename:"PowerShell.EXE")`; `HashBypassBinaries` includes
  `WindowsPowerShell\v1.0\powershell.exe` (Windows-only).

### M9 — Medium — Web-filter coverage is frozen at service start, and the PAC's `DIRECT` fallback can be forced

- **Where:** `WindowsServicePorts.cs:91` (`BrowserLocator.ForCurrentMachine().Locate()` runs once), `WfpFilterPlan.cs:67-105` (filters keyed by exact exe path), `PacFile.cs:21`
  (`PROXY ...; DIRECT`), `WebFilterProxy.cs:164,243,413` (the idle timer is re-armed on every read), `WebFilterProxyOptions.cs:21,24` (512 connections, 2 min idle).
- **Scenario:** a browser installed after the service started (a per-user Chrome/Brave/Firefox install needs no admin) or in a custom location has no WFP block filters until the service
  restarts; if the user allowlists it, only the policy-driven PAC remains. A local process holds the proxy's 512 slots open with one byte per 119 s; further browser connections are
  dropped (`client.Dispose()`), Chromium treats that as a proxy failure and falls back to `DIRECT` — which nothing blocks for the uncovered executable. Non-listed browsers (Opera, Vivaldi
  are in the WFP list but have no policy; Tor Browser, Waterfox, portable builds) rely on the process allowlist alone.
- **Fix:** recompute the browser list at every session start (`BuildRuntime`/`BringUpAsync`), also from the allowlist (any allowlisted exe whose version info says "Chrome/Firefox/..." or that
  loads a known browser DLL); give the proxy an absolute header deadline (10 s) and a per-client-process cap (query the owning pid with `GetExtendedTcpTable`, or cap by loopback source port count);
  reserve slots for established tunnels; consider dropping `; DIRECT` while the WFP session is healthy and letting the supervisor republish a DIRECT PAC on service death.
- **Tests:** `WebFilterProxyTests`: a slow header sender is closed after the absolute deadline; 512 trickling clients cannot starve a 513th (per-source cap). `BrowserLocatorTests`: new
  per-user install appears on the next `Locate()`.

### M10 — Medium — IPC authorization is "any authenticated local process"; slots, scans and logs can be exhausted by any user

- **Where:** `WindowsCallerIdentityResolver.cs:75-78,236-243` (every client of any account gets `User`), `SecurePipeListenerFactory.cs:167` (`AuthenticatedUserSid`),
  `IpcServer.cs:13,336` (32 connections shared by UI, agent, stub), `IpcMethodPolicy.cs:119-121` (`allowlist.scanApps` allowed during sessions), `AppInventoryScanner.cs:107`
  (hashes every executable on each scan, not cached), `IpcServer.cs:396,407` (every rejected call is logged).
- **Scenario:** (a) another account on the machine, or any process, starts a 30-day session (`session.start` is `User`); (b) 32 idle pipe connections (there is no idle timeout and
  the limit is shared) keep the real UI, block screen and agent out — the emergency-pass button then cannot work; (c) `allowlist.scanApps` in a loop (50/s per connection) saturates disk and
  CPU even during a session; (d) log spam (see M15).
- **Fix:** bind sensitive methods to the interactive console user (store the SID that owns the session; `session.start`, `allowlist.set`, `session.useEmergencyPass` require
  `UserSid == console user`); per-role connection quotas (reserve slots for Agent/Stub), 5-minute idle disconnect for connections that made no call, per-*identity* (not per-connection)
  rate buckets with a cost per method (scan = 30 tokens, cache the result for 60 s), explicit deny ACE for `NETWORK` (S-1-5-2) in the pipe DACL.
- **Tests:** `IpcServerTests`: 32 idle connections from role `User` do not block an `Agent` connection; per-identity bucket survives reconnects; `ServiceIpcHandlersTests`: second SID cannot start a
  session or use a pass.

### M11 — Medium — Unthrottled break-glass attempts: PBKDF2 under global gates, no lockout, no audit

- **Where:** `SessionStateMachine.cs:173-186` (verification inside `_gate`), `EnforcementCoordinator.cs:227-246` (inside the coordinator gate), `BreakGlassCode.cs:41-48`
  (210 000 iterations, about 50-100 ms CPU), `IpcMethodPolicy.cs:119` (`User` role, allowed during session).
- **Scenario:** 32 connections × 50 requests/s of wrong codes keep the thread pool and both gates busy; `TickAsync`, emergency passes, start/stop and process-block bookkeeping that needs
  the gates stall, and the pool starvation slows the process pipeline too (M5). The 100-bit code itself cannot be guessed, but nothing records the attempts.
- **Fix:** global failed-attempt limiter (for example 5 failures then 30 s, doubling to 15 min; independent of the connection), derive the PBKDF2 outside both gates on a single dedicated
  worker, log every failure with the caller pid/SID, publish a notification after the third failure.
- **Tests:** `SessionStateMachineTests`/`CoordinatorSessionTests`: after 5 wrong codes the sixth call returns without running PBKDF2 (fake iteration counter); a correct code during lockout
  is still refused until the delay passes.

### M12 — Medium — The service trusts the agent-supplied pid and image path and terminates as SYSTEM

- **Where:** `ServiceIpcHandlers.cs:352-374` (`agent.foregroundWindow`: pid, path, start ticks from the client), `EnforcementCoordinator.Processes.cs:43-55`
  (`BlockWindowProcessAsync` checks criticality on the *claimed* path), `Win32ProcessController.cs:229-263` (re-checks only start time and the real image's criticality),
  `AgentLink.cs:45-54` (last agent to register wins), `WindowsCallerIdentityResolver.cs:308-325` (agent role = image path in the install directory).
- **Scenario:** any user can start another `Blocker.UI.exe --agent` (or inject into the running one) and register as *the* agent. With a window-interceptor plugin installed, the fake agent reports
  a window of any pid with a path the plugin vetoes; the service terminates that pid as SYSTEM: another user's process or a non-critical SYSTEM process (VPN client, backup agent, third-party AV).
- **Fix:** the service resolves path/session/owner of the pid itself and requires `claimed == real`, same terminal session as the agent, and the same user SID as the agent; accept `agent.register`
  only from the console session and keep the first live agent per session.
- **Tests:** `CoordinatorProcessTests`: `BlockWindowProcessAsync` with a mismatching path or a pid in another session does not terminate; `AgentLinkTests`: a second registration from a different session
  is refused.

### M13 — Medium — Every update deletes all plugin data

- **Where:** `src/Blocker.Setup/SetupEngine.cs:96-103` (update = `--uninstall` of the old version), `src/Blocker.Service/ServiceCommands.cs:126-141` (`--uninstall` ends with
  `ports.PluginData.Remove(paths.PluginDataDirectory)`).
- **Scenario:** installing a newer `WindowsDetox-Setup.exe` over an existing install removes `ProgramData\WindowsDetox.PluginData` (Goals tasks, Calendar events and history, Focus Insights),
  while the installed plugin packages in `DataRoot\plugins` survive and now start with empty storage. The test `Uninstall_removes_services_and_then_the_plugin_data` encodes the behavior
  for a real uninstall but not for the upgrade path.
- **Fix:** split the command: `--uninstall` (full, as today) and `--remove-services` / `--uninstall --keep-data` for the upgrade; the installer uses the latter; `--install` re-provisions
  the data directory only when missing.
- **Tests:** `ServiceCommandsTests`: `Upgrade_keeps_the_plugin_data`; `SetupEngine` integration test (Windows CI) that data written before an update is readable after it.

### M14 — Medium — The installer ignores unexpected `--uninstall` exit codes and then deletes the files, the data directory and the uninstall entry

- **Status:** Fixed in 0f182dd (`ServiceUninstallCheck`: only exit code 0 proceeds). Not fixed: staging the update into a new directory and swapping. Verify on VM: make `Blocker.Service.exe --uninstall` fail (stop the SCM service from stopping) and confirm Setup aborts with files, data directory and the Add/Remove entry intact.
- **Where:** `SetupEngine.cs:98-109` (install/update path) and `:138-149` (uninstall path) handle only exit codes 3 and 4; `ServiceCommands.cs:88-92` returns `Failed (1)` for any
  exception (service did not stop in 30 s, DPAPI failure, missing handler...) and `BadArguments (2)`/`NotAdministrator (5)` for others; `SetupEngine.cs:162` then runs
  `DeleteTree(InstallDirectory) | DeleteTree(DataDirectory)`.
- **Scenario:** `--uninstall` rolled nothing back or could not remove a service, Setup carries on: install directory and `ProgramData\WindowsDetox` (journal, `hmac.key`, `install.id`) are
  deleted, the Add/Remove entry is gone, but the services stay registered (they fail at every boot, pointing to a deleted exe) and any IFEO redirect or service DACL that was still applied
  now points to a missing stub and can no longer be swept (the id that marks our artifacts is deleted): installed apps fail with "file not found" until someone edits the registry.
  On the update path the same ignored code lets a new payload be extracted over a half-removed install.
- **Fix:** treat every non-zero code as failure, abort before any file or registry deletion, and tell the user to run `--recover` or reboot; only exit code 0 may proceed. Additionally
  delete obsolete files of the old version before extraction (stage into a new directory and swap) so an interrupted update does not mix versions.
- **Tests:** `SetupEngine` tests with a fake `RunServiceCommandAsync`: codes 1, 2, 5 leave all files and registry values untouched and throw `SetupException`.

### M15 — Medium — Log flooding by any local user can fill the system disk

- **Where:** `RollingFileLoggerProvider.cs:16,19,90,132` (5 MB per file, 14 days, but an unbounded number of `service-yyyyMMdd.N.log` files per day), `IpcServer.cs:396,407,462`
  (a warning per rate-limited/unauthorized/failed request), `WebFilterProxy.cs:139,377` (debug).
- **Scenario:** a loop of rejected requests on 32 connections at 50 requests/s writes roughly 300 KB/s (about 25 GB/day) into `ProgramData`; when the system volume is full,
  `AtomicFile` writes of the journal and `session.bin` fail, so applies and session ticks start failing.
- **Fix:** cap total log bytes (for example 200 MB, delete oldest first), rate-limit repetitive messages per template (log every 100th and a counter), and never log per rejected request
  above `Debug` after the first N per peer per minute.
- **Tests:** `RollingFileLoggerProviderTests`: after N MB the oldest files are removed and the total stays below the cap.

### M16 — Low — Plugins can be side-loaded without an administrator (unauthenticated `package.zip` and `state.json`)

- **Where:** `src/Blocker.Core/PluginRuntime/PluginStore.cs:43-63,200-209` (installed = a directory name with a `package.zip`; enabled = a plain JSON file), `PluginHostRuntime.cs:512-538`
  (the package is rebuilt and policy-checked at every load, so the sandbox rules still hold).
- **Scenario:** the same ACL gap as M1 lets a standard user create `DataRoot\plugins\<id>\package.zip` + `state.json` between sessions. That skips the admin gate, the install tests and the
  lock-policy vetoes of `plugins.install`. Impact is limited because the load path re-runs package validation and API policy and plugins can only tighten.
- **Fix:** M1's DACL; additionally keep an HMAC-sealed registry of installed package hashes (`plugins.bin`) and ignore directories that are not in it.
- **Tests:** `PluginStoreTests`: a directory created outside `InstallAsync` is not listed.

### M17 — Low — `BootMarker` is derived from the wall clock in 30 s buckets and recomputed on every process start

- **Where:** `WindowsServicePorts.cs:124-129`, consumed by `SessionStateMachine.cs:253-254` and `SessionClock.cs:44`.
- **Scenario:** `floor(utc_s) - floor(tick_s)` jitters by ±1 s, so about 1 in 30 service restarts crosses a bucket boundary; a wall-clock step (NTP correction, resume from sleep, manual change) of
  more than the bucket size does too. The state machine then sees a "reboot": without verified time the downtime is not counted (the session is extended) and a running emergency pass ends.
  The failure is conservative but unpredictable.
- **Fix:** use a boot identity that does not depend on the wall clock (**Windows**: the kernel boot GUID from `NtQuerySystemInformation(SystemBootEnvironmentInformation)`, or the
  `BootId` of the memory-management prefetch parameters); persist it with the session and compare for equality. Verify its behavior with Fast Startup and hibernate (see Hardening H7).
- **Tests:** `SessionClockTests`: equal marker + larger tick counts the downtime; a different marker does not. Windows-only: the identity is stable across a service restart.

### M18 — Low — The break-glass code is lost if the start response is not delivered

- **Where:** `ServiceIpcHandlers.cs:210` (returned once), `IpcConnection.cs:311-322` (a failed response write is only logged), `ServiceIpcHandlers.cs:146-169` (plugin-started sessions drop the code by
  design; capped at 12 h by `SessionLaunchLimits.MaxDuration`).
- **Scenario:** the UI crashes or closes while `session.start` is in flight: the session is running (up to 30 days), the code is gone, and neither the UI nor `--recover` can end it
  (`--recover` refuses while a session is persisted).
- **Fix:** keep the plaintext code in service memory until the client acknowledges (`session.ackBreakGlass`) and re-deliver it on the next `session.getStatus` of the same peer identity; if no
  acknowledgement arrives within 60 s end the session as `BreakGlass` (the existing failed-start path). For plugin sessions document that the pass is the only way out.
- **Tests:** `ServiceIpcHandlersTests`: a response write failure ends the session; an acknowledged start keeps it.

### M19 — Low — A faulted plugin that spins keeps burning a CPU core; the plugin host has no CPU cap; "Unavailable" is permanent and killable by the user

- **Where:** `LoadedPlugin.cs:81-86` (`Task.Run` of the plugin callback: the breaker abandons it, but a synchronous infinite loop keeps its pool thread), `RestrictedProcessLauncher.cs:141-163`
  (job has memory, process-count and kill-on-close limits, no CPU rate limit), `PluginHostSupervisor.cs:470-476` (after 5 crashes in 10 minutes the supervisor returns for good).
- **Scenario:** a buggy or hostile plugin loops synchronously in an interceptor: it is faulted after a few budget timeouts but the thread spins until the host dies. A user who can kill their own
  low-integrity `Blocker.PluginHost.exe` six times in ten minutes switches every plugin veto and interceptor off until the service restarts (documented fail-open: `faulted contributors count as Allow`).
- **Fix:** add `JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP` (for example 20 %), restart the host when a plugin is faulted with an abandoned call still running, and retry the host launch after a cool-down (for
  example every 10 minutes) instead of staying `Unavailable` until restart.
- **Tests:** `PluginHostSupervisorTests`: after `Unavailable` and the cool-down the host is launched again. `PluginCircuitBreakerTests`: abandoned-call counter triggers the restart request.

### M20 — Low — Plugin session-start and toast limits use the wall clock and live only in memory

- **Where:** `PluginHostRequestHandlers.cs:164,205` (`deps.Time.GetUtcNow()` with `TimeProvider.System`), `:42-43` (dictionaries reset on service restart).
- **Scenario:** changing the clock (admin) or restarting the service resets the per-plugin 16 h/day and the hourly toast limits. Bounded by the 12 h per session cap, so impact is small.
- **Fix:** use the monotonic/verified clock (`IVerifiedClock`, fall back to `Stopwatch` timestamps) and persist the counters in the sealed store.
- **Tests:** `PluginHostRequestHandlersTests`: changing the fake wall clock does not reset the limit.

---

## Hardening improvements (not bugs)

- **H1 — Owner implicit rights.** The restricted service DACL (`ServiceSddl.cs:31-32`) and the NTFS deny ACEs (`NtfsDenyAcl`) do not stop the *owner* (BUILTIN\Administrators) from rewriting the DACL, because
  owner rights (`READ_CONTROL`, `WRITE_DAC`) are implicit unless an `OWNER RIGHTS` ACE is present. Add `(A;;RC;;;OW)` to both. It does not stop a determined elevated admin but removes the cheapest one-click path
  (**Windows**, verify with `sc sdset` as an elevated admin during a session).
- **H2 — Explicit DACL on `DataRoot`** (also M1 fix 1): create it at install with SYSTEM/Administrators only and re-assert it in `BlockerServiceApp.StartAsync`.
- **H3 — Pipe lifecycle.** `Server.Start()` runs after `RecoverAsync` (`BlockerServiceApp.cs:268-269`). If the first-instance create fails (pipe squatted after a crash), `StartAsync` throws, `DisposeAsync` tears the
  already-running enforcement down and the SCM restart loop repeats. Start the pipe first, retry with back-off, and keep enforcing while it is unavailable. Add a deny ACE for `NETWORK` in the DACL.
- **H4 — Service dependencies.** `CreateService` passes no dependencies (`ScmServiceInstaller.cs:109-121`); WFP needs `BFE`, the plugin host needs a user session. Declare `DependOnService=BFE`, set `SERVICE_PRESHUTDOWN`
  and keep the retry loop of `ResumeAsync` as is (it already recovers, but the first apply at boot fails and is rolled back and re-applied 10 s later).
- **H5 — Loop supervision.** `RunGuardedAsync` (`BlockerServiceApp.Runtime.cs:115-129`) logs and ends a failed background loop for good (time refresh, status, bundled plugins). Wrap loops in restart-with-backoff and add a
  heartbeat that surfaces `ProtectionDegraded` when the proxy accept loop, the ETW session or the IPC accept loop is gone (`WebFilterProxy.cs:92-120` ends silently on non-socket exceptions and busy-loops on
  repeated `SocketException`).
- **H6 — Fail-open on a damaged `session.bin` is by design** (`BlockerServiceApp.Runtime.cs:33-45`), so one corrupt file ends the session. Keep a second sealed copy (`session.bin.1`) written alternately so a
  single bad write or AV truncation does not end the session, and write tamper/break-glass/rollback-incomplete events to the Windows Event Log.
- **H7 — Verify power-state semantics on the VM.** `Environment.TickCount64` behavior across sleep, hibernate and Fast Startup (hybrid shutdown) decides whether powered-off time counts and whether `BootMarker` changes
  (`SessionClock.cs:44-52`). Check: shut down with Fast Startup on, boot, read `Remaining`; suspend 10 minutes, read `Remaining`. Consider `QueryUnbiasedInterruptTime` plus explicit power events.
- **H8 — Allowlist hygiene.** Reject path-only allowlist entries whose directory is user-writable (`Allowlist.cs:275-278`), and recompute hashes of allowlisted apps at session start so an auto-update between scan and session does
  not silently turn an allowed app into a blocked one. Refresh the installed-app scan at session start so IFEO covers newly installed apps (`EnforcementCoordinator.cs:187` ignores updates while idle only through UI scans).
- **H9 — Wildcard guard.** `DomainPattern.TryParse` accepts `*.co.uk`/`*.github.io` (`DomainMatcher.cs:166-177`); reject public-suffix wildcards or warn.
- **H10 — ECH.** Chromium/Firefox with Encrypted Client Hello send an outer SNI that differs from the `CONNECT` host, which `WebFilterProxy.cs:253-257` refuses (fails closed, but breaks allowed Cloudflare sites).
  Set `EncryptedClientHelloEnabled=0` (Chromium) and `network.dns.echconfig.enabled` through the policy plan.
- **H11 — Extra trusted roots.** `WindowsServicePorts.cs:37` trusts `ProgramData\Microsoft\Windows Defender` for critical names; confirm on the VM that Users cannot create files there (`icacls`), or restrict to
  `Platform\<version>\` and require the signature check from M2.
- **H12 — Interceptor cost.** `DecideAsync` calls `IsMicrosoftSigned`, `GetPackageFamilyName` and the plugin interceptor for every non-trusted process; cache verdicts per (image, mtime, length) and skip the interceptor
  for repeat launches inside a short window.
- **H13 — Time sources.** `HttpsDateTimeSource` and the SNTP sources use the system proxy settings and DNS of the SYSTEM account; pin `UseProxy=false` and consider requiring one HTTPS source in every consensus (two UDP
  sources alone can be spoofed by anyone on the path).

## Checked and fine (no action)

Frame reader validates the length before allocating and per-role limits bound it (`FrameCodec.cs:59-70`); strict DTO parsing, max depth 32; HMAC verified in constant time before DPAPI; journal is write-ahead and idempotent,
first original kept; session clock is monotonic and never moves backwards; emergency-pass day key comes from verified UTC in a frozen zone and DST/midnight rollovers cannot grant extra passes; plugin ZIP reader counts real decompressed
bytes, rejects traversal/ADS/duplicates; domain normalization (IDN, trailing dot, IP literals) and SNI-equals-CONNECT-host check; PluginId path safety; WFP dynamic session removes filters with the service; service-DACL sweep now has
defaults; `HmacKeyStore` no longer overwrites a key it cannot unprotect.

## Test gaps (summary)

No test covers: critical names in user-writable `%SystemRoot%` subtrees; spoofed parent; dropped ETW events or a dead ETW session; a stalled hasher; the DST-ambiguous start time; teardown with a failing rollback followed by a retry; break-glass
throttling; second-user `session.start`; per-identity IPC limits; setup exit-code handling; upgrade keeping plugin data; log size cap; forged journal entries for `servicedacl`; PE `OriginalFilename` matching.
