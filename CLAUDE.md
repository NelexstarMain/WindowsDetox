# WindowsDetox — project instructions

Distraction blocker for Windows 10/11 (.NET 10, WPF, Windows service, plugin SDK). Architecture: `docs/ARCHITECTURE.md`.

## Language contract
- Reply to the user in **Polish**. Code, identifiers, comments, this file, `.claude/handoff.md` and commits in **English**.

## Rules and workflow (Claude Code Hygiene System, vendored from NelexstarMain/ClaudeCode)
- Follow every rule in `.claude/rules/` (discipline/YAGNI, pragmatic TDD, clean repo).
- Workflow skills: `/core-kickoff`, `/core-tests`, `/core-subagents`, `/core-memory`, `/core-hygiene`, `/core-wrapup`; .NET reference: `/csharp`.
- Hooks in `.claude/settings.json`: Stop is a TDD gate — after editing production code, run a build or test before ending the turn.
- At >= 50% context: finish the step, `/core-wrapup` (writes `.claude/handoff.md`), then `/clear`.
- Hook self-tests: `python3 -m unittest discover -s .claude/tests -v`.

## Subagent rules (project spec)
- Main thread (architecture, SDK contracts, Windows security decisions, integration, final audits): Opus.
- Subagents (isolated multi-file modules, tests, reading references, independent review): Sonnet (`model: sonnet`).
- Delegate only well-scoped multi-file or parallel work with exact paths, acceptance criteria and an output ceiling
  (<= 20 lines, `file:line` refs); no subagents for single-file fixes. Read-only unless an isolated write is assigned.
- Effort per task: low/medium for scaffolding, high/xhigh for ETW, WFP, DACL, Rollback Journal, Roslyn, security audits.
- End of each stage: verify with `dotnet-slopwatch`, tests (`run-tests`) and `security-and-hardening`.

## Build and test
```bash
dotnet build WindowsDetox.slnx
dotnet test --solution WindowsDetox.slnx --filter-not-trait "Category=WindowsOnly"   # Linux container
dotnet slopwatch analyze -d . --fail-on warning
eng/build-installer.sh Release   # out/WindowsDetox-Setup.exe
```
- `eng/render.sh [--only a,b]` renders the real UI with fake data to `artifacts/renders/*.png` under Wine (see `/ui-render`).
  Scenarios with `"animate": true` also write `anim/<name>-strip.png` (numbered filmstrip, review this) and `<name>.gif`.
  Review images downscaled (`convert x.png -resize 50% ...`) to save context. Wine always runs under `env -i` (the script does it).
- `tests/Blocker.UI.Tests` targets `net10.0-windows`: it builds here but runs only in CI, so it is not in the Linux test count.
- UI review loop: a critic agent renders and reviews (static + animated shots), a fixer agent in a worktree applies the
  findings; the main thread merges, verifies, renders and pushes. Remove merged, clean worktrees afterwards (disk is limited).

## Plugin UI contract
- Plugins draw declarative `UiNode` trees (`src/Blocker.UI.SDK`), validated by `UiTreeValidator` and drawn by
  `src/Blocker.UI.Controls/Rendering`. SDK API lives in `PublicAPI.Unshipped.txt`: additive changes only, then
  `eng/update-public-api.sh <proj>`. UiAction shapes are enforced in `src/Blocker.Core/PluginRuntime/UiContributionSupport.cs`.

## Release
- Bump `<Version>` in `src/Blocker.Setup/Blocker.Setup.csproj` and `src/Blocker.UI/Blocker.UI.csproj`, add
  `docs/releases/v<version>.md`, push: CI (windows-latest) builds and install-tests the installer, then creates release
  `v<version>` once. `latest-build` is refreshed on every push.
- The remote branch gets "Update UI screenshots from CI" commits: `git fetch origin <branch> && git merge --no-edit FETCH_HEAD` before pushing.
- Linux builds WPF via `EnableWindowsTargeting`; Windows-only behavior (ETW/WFP/IFEO/DACL/UI) runs in CI `windows-latest` and in a VM (`docs/VM_TESTING.md`).

<!-- HYGIENE:AUTO -->
<!-- /HYGIENE:AUTO -->
