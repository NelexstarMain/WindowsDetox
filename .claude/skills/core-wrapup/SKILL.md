---
name: core-wrapup
description: End-of-stage wrap-up ritual before /clear. Run manually when closing a milestone or when context reaches 50%. Verifies cleanliness/tests and writes a ~1400-1600 token .claude/handoff.md for lossless resumption.
disable-model-invocation: true
user-invocable: true
argument-hint: "[optional-focus-note]"
---

# Core Wrap-Up Ritual (Pre-`/clear`)

Optional focus note from user: `$ARGUMENTS`

## Live Repository Snapshot (Preprocessed)
!`git status -sb 2>/dev/null || echo "(no git repo)"`
!`git diff --stat 2>/dev/null | tail -n 30 || true`

---

## Step 1: Final Verification & Cleanliness Check
1. Review the preprocessed `git status` / `git diff --stat` above for accidental scratch files, temporary debug code, or unfinished edits.
2. Run the project's verification command (`dotnet test`, `dotnet build`, `pytest`, etc.) and capture the exact command and observed pass/fail counts.
3. If durable architectural invariants or new verification commands were established in this stage, update the manual section of `./CLAUDE.md` concisely (strictly **above** `<!-- HYGIENE:AUTO -->`).

## Step 2: Write `.claude/handoff.md` (~1400–1600 Tokens, English)
Create or overwrite `./.claude/handoff.md` in **English**. Target **~1400–1600 tokens** of dense, concrete technical state (no fluff) using all 7 sections below:

```markdown
# Session Handoff

## 1. Primary Goal & Scope
- End goal, active milestone, and explicit non-goals.

## 2. Completed in This Stage
- Concrete implemented behaviors, endpoints, UI components, or bug fixes (with exact file paths).

## 3. Architectural Decisions & Invariants
- Key trade-offs made, YAGNI boundaries respected, data/schema or concurrency invariants.

## 4. Key Contracts & Verification Commands
- Exact DTOs, public interfaces, DB tables, or method signatures the next session must preserve (include short signatures/snippets).
- Exact build/test/run commands and their latest observed output/status.

## 5. Encountered Problems, Pitfalls & Open Risks
- Bugs hit, subtle framework quirks (Blazor/EF Core/Unity/Godot/Docker), and unresolved edge cases or tech debt.

## 6. Modified Files
- Concise bullet list of touched paths and what changed in each.

## 7. Exact Next Step
- The single unambiguous first action (and first Red test or verification command) to execute immediately after `/clear`.
```

## Step 3: Report & Hand Control to User (Polish)
Summarize briefly in **Polish**:
- Verification status (command + result),
- What was captured in `.claude/handoff.md`,
- Explicit confirmation that the user can now safely run `/clear` (the `SessionStart` hook will automatically inject `.claude/handoff.md` into the clean session and archive it to `.claude/handoff.consumed.md`).
