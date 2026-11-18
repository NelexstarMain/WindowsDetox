# Windows Detox

Distraction blocker for Windows 10/11. You choose an allowlist of apps and websites and start a **detox session**:
until the timer reaches zero, everything else is blocked, and the session cannot be shortened or cancelled.
Emergency passes (X minutes, Y per day) pause blocking briefly.

## Install
1. Download `WindowsDetox-Setup.exe`, from the **WindowsDetox-Setup** artifact of the latest CI run on GitHub
   Actions, or build it yourself (below).
2. Run it and approve the administrator prompt. It installs to `C:\Program Files\WindowsDetox`, registers two
   services (`WindowsDetoxService` and `WindowsDetoxWatchdog`), and adds a Start-menu shortcut.
3. Open **Windows Detox** from the Start menu. Build an allowlist, then start a session from **Setup**.

Uninstall from **Settings → Apps → Installed apps → Windows Detox**, or run
`C:\Program Files\WindowsDetox\Blocker.Setup.exe --uninstall`. Both are refused while a session is running.
Uninstalling undoes every change the app made.

> The installer is not code-signed, so Windows SmartScreen may warn. Choose *More info → Run anyway*.
> Try the app in a VM first: see [docs/VM_TESTING.md](docs/VM_TESTING.md).

## Build
Requires the .NET 10 SDK. Works on Windows, or on Linux with bash and python3:

```bash
eng/build-installer.sh Release
```

The output is `out/WindowsDetox-Setup.exe`. Building with `Debug` instead produces a developer build in which
`Blocker.Service.exe --dev-abort` can end a session.

```bash
dotnet build WindowsDetox.slnx
```

```bash
dotnet test --solution WindowsDetox.slnx
```

## Docs
- [Architecture and security decisions](docs/ARCHITECTURE.md)
- [Safe testing in a VM](docs/VM_TESTING.md)
- Plugin authoring: download the starter template from the **Plugins** page.
