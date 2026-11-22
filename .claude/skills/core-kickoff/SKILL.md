---
name: core-kickoff
description: Session and project kickoff ritual. Run manually before coding on a new project, major feature, or unfamiliar repository to interview the user, run a lightweight Haiku scout, tailor local .claude/ rules/skills without modifying global skills, write CLAUDE.md, and verify tests.
disable-model-invocation: true
user-invocable: true
argument-hint: "[project-or-task-goal]"
---

# Core Kickoff Workflow

Goal context (if provided): `$ARGUMENTS`

## Live Environment Snapshot (Preprocessed)
Global skills available:
!`ls -1 ~/.claude/skills/ 2>/dev/null || echo "(none)"`

Current project root & `.claude/` directory:
!`ls -la 2>/dev/null | head -n 25`
!`ls -la .claude 2>/dev/null || echo "(no local .claude/ yet)"`

---

## Execution Protocol
Communicate with the user in **Polish**; write all generated repository files (`CLAUDE.md`, local `.claude/rules/`, local `.claude/skills/`, code) in **English**.

### Step 1: Lightweight Subagent Reconnaissance (`repo-recon`)
Before asking redundant questions about files already on disk, spawn the **`repo-recon`** subagent (Haiku, read-only) — or `Explore` if `repo-recon` is unavailable — to scan the repository in <= 25 lines.
*(Skip only if the directory is completely empty).*

### Step 2: Adaptive Interview (Polish)
Using what `repo-recon` found (so you never ask obvious questions), ask ONE crisp, numbered batch of questions in **Polish**:
- **Small / well-defined task (4–5 questions):** Exact goal & non-goals, invariants/edge cases, UI/engine or DB impact, definition of done.
- **New project / major feature (8–10 questions):** Core problem & user flow, domain boundaries & data model, persistence/EF Core/SQL needs, UI/engine constraints (Blazor render mode, WPF/MAUI lifecycle, Unity/Godot tick loop), concurrency/async boundaries, security/secrets handling, test strategy, and explicit non-goals.

**Stop and wait** for the user's answers before Step 3.

### Step 3: Local Skill & Rule Adaptation (Read-Only Global Base)
- **Strict isolation:** NEVER modify `~/.claude/skills/`, `~/.claude/rules/`, or `~/.claude/agents/`.
- Review existing global skills and any local `.claude/rules/` or `.claude/skills/`.
- Eliminate duplicates and generate **project-local** files inside `./.claude/` ONLY where the project adds real constraints not already covered globally:
  - Prefer path-scoped rules in `./.claude/rules/*.md` using YAML `paths:` frontmatter (e.g., `paths: ["src/**/*.cs"]`, `paths: ["**/*.razor"]`, `paths: ["tests/**"]`) so rules only load when relevant files are touched.
  - Create local `./.claude/skills/<name>/SKILL.md` only for recurring project-specific workflows (e.g., migrations, Docker compose dev workflow, custom engine export).
  - If the repository uses custom verification commands not in the global hook defaults (e.g., `./scripts/verify.sh`, `tox`, `sqlfluff`), or is a pure non-code/docs repo, create `./.claude/hygiene.json` with `extra_test_commands`, `extra_ignored_paths`, or `disable_tdd_gate`.

### Step 4: Generate or Refine Project `./CLAUDE.md` (English)
Keep `./CLAUDE.md` lean and high-signal — **do NOT include generic language advice or anything Claude can infer by reading one file**. Include only:
1. Project purpose & architecture boundaries (3–6 lines).
2. Exact build, test, lint, and run commands.
3. Non-obvious invariants, legacy traps, or strict constraints.
4. Preserve (or append) the protected background-hygiene block at the bottom:
```markdown
<!-- HYGIENE:AUTO -->
<!-- Managed automatically by background hygiene. Do not edit manually inside this block. -->
<!-- /HYGIENE:AUTO -->
```

### Step 5: Verify Test Runner & Present Plan
Run the detected test/build command (or initialize the test setup if requested) to verify a working baseline. Present a short summary in **Polish** of what was configured and the first TDD step (Red test to write next).
