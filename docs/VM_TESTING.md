# Testing WindowsDetox safely in a VM

WindowsDetox is designed to be hard to stop while a session is running. Try it in a disposable Windows 10/11 VM
before you put it on a real machine. This guide sets up a VM where you can always get out.

## 1. Prepare the VM
1. Install Windows 10 22H2 or Windows 11 in Hyper-V, VMware or VirtualBox. Give it at least 4 GB of RAM.
2. Create two accounts: an **administrator** for installing and recovery, and a **standard user** for daily testing.
   This is the configuration the threat model assumes.
3. **Take a snapshot now.** If anything goes wrong, reverting the snapshot is the guaranteed way out.

## 2. Build a developer-mode installer
Developer abort exists only in **Debug** builds, which define the `DEVMODE` symbol. Release builds cannot abort
a session.

```bash
eng/build-installer.sh Debug
```

Copy `out/WindowsDetox-Setup.exe` into the VM. Do not run it yet: create the dev key first (step 3).

## 3. Turn developer mode on
Developer mode needs three things to be true:
- a Debug build;
- the `--dev-mode` flag;
- the file `C:\ProgramData\WindowsDetox\devmode.key` (any content), which only an administrator can create.

In an elevated terminal:

```bash
mkdir C:\ProgramData\WindowsDetox
```

```bash
echo dev > C:\ProgramData\WindowsDetox\devmode.key
```

Run `WindowsDetox-Setup.exe`, then re-register the service with the developer flag:

```bash
"C:\Program Files\WindowsDetox\Blocker.Service.exe" --install --dev-mode
```

This command updates and restarts the two services, `WindowsDetoxService` and `WindowsDetoxWatchdog`. If you want to
watch the logs live instead, run the service in a console:

```bash
"C:\Program Files\WindowsDetox\Blocker.Service.exe" --console --dev-mode
```

Logs are written to `C:\ProgramData\WindowsDetox\logs`.

## 4. Run the apps
- `Blocker.UI.exe`: the main window. Set up the allowlist and start a session here.
- `Blocker.UI.exe --agent`: the per-user agent. It provides the tray icon and notifications, and closes
  windows gracefully.

Start with a short session, about 5 minutes, and a small allowlist.

## 5. Ways out, from mildest to strongest

| Situation | What to do |
|---|---|
| Developer build, session running | In an elevated terminal, run `Blocker.Service.exe --dev-abort`. The session ends and every change is rolled back. |
| Any build, session running | Use the **break-glass code** shown once at session start (enter it on the dashboard). The session ends with a full rollback. Write the code down. |
| Service or journal damaged, no session running | Run `Blocker.Service.exe --recover` as administrator. It replays the journal, then sweeps every artifact marked `BlockerOwned=<installId>`. |
| Removing the app | Run `Blocker.Service.exe --uninstall`. It is refused during a session. Otherwise it rolls back, sweeps, and removes both services. |
| Nothing works | Revert the VM snapshot. |

`--recover` and `--uninstall` refuse to run while a session is active. This is deliberate: deleting state must
never shorten a session. Use break-glass or `--dev-abort` for that.

## 6. What to check (all of these have only compiled, never run, in CI on Linux)
- **Process blocking:** a non-allowlisted app is closed and the block screen (Stub) appears; allowlisted apps and
  their child processes keep running; Task Manager, `regedit` and `cmd` are blocked; critical Windows processes are
  never touched.
- **Web filtering:** in Edge, Chrome and Firefox, sites not on the allowlist show the block page; DoH and QUIC are
  off; after the session ends, browsing works normally and `edge://policy` / `chrome://policy` no longer list the
  WindowsDetox policies.
- **Tamper resistance:** stopping either service from `services.msc` fails or the service restarts within a
  second; the `C:\ProgramData\WindowsDetox` files cannot be deleted by the standard user.
- **Emergency pass:** the warning arrives 60 s before the end; disallowed apps close when the pass ends; the pass
  count resets at local midnight only after the time has been verified online.
- **Time:** changing the clock in Settings does not shorten a session. Rebooting while offline does not count the
  time the VM was powered off.
- **No traces:** after the session ends, check that nothing is left behind:
  - `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options` (and the `WOW6432Node` view)
    has no entries with `BlockerOwned`;
  - the service DACL is back to its original value;
  - `C:\ProgramData\WindowsDetox` has no deny entries;
  - `C:\Windows\System32\drivers\etc\hosts` is unchanged.
- **Plugins:** the starter template ZIP installs and its tests pass; installing, removing or disabling a plugin is
  refused while a session runs.

## Known limitations
- A determined administrator can always win: Safe Mode, boot media, or taking ownership of files. The app targets
  impulsive bypass. Use a standard account day to day.
- The ETW process start time is converted from local time, so process-tree inheritance can be ambiguous during the
  DST changeover hour.
- The Windows-specific code (WPF UI, WFP, pipe ACLs, IFEO, SCM, restricted token, SkSL shader) has never been
  executed. Treat the first VM run as an integration test.
