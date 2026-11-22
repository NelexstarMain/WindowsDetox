# Global User Preferences (Max 70 Lines)

## 1. Communication & Language
- User communication language: Polish (concise, technical, direct).
- Code, identifiers, comments, `CLAUDE.md`, `.claude/handoff.md`, and commits: English.

## 2. Architecture & Code Style
- Strict YAGNI: avoid speculative interfaces, factories, and wrappers for a single implementation (except explicit I/O boundaries).
- Self-documenting code: add comments only to explain *why* (non-obvious invariants, trade-offs, workarounds), never *what*.
- Strict task scope: do not refactor unrelated neighbor code; briefly report noticed issues instead.
- Primary stacks: C# / .NET (Blazor, WPF, MAUI, Unity/Godot), SQL / EF Core, Python, Docker / GitHub Actions.

## 3. Testing & Verification
- Pragmatic TDD: write a reproducing failing test first (Red) for business logic, algorithms, and bug fixes.
- Auto-detect the repository's test framework before adding tests; verify with real test runs before concluding.
- Keep `Stop` smart TDD gate satisfied: run relevant tests or build after modifying production code.

## 4. Context & Hygiene Habits
- Keep `~/.claude/rules/` ultra-compact and `CLAUDE.md` concise.
- Use subagents to isolate noisy file searches and test output from the main context window.
- When context reaches 50%, finish the active step, run `/core-wrapup`, and reset with `/clear`.
- Never auto-delete repository files in background tasks; log hygiene findings to `.claude/hygiene-report.md`.
