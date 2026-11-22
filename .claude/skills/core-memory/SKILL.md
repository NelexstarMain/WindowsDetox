---
name: core-memory
description: Three-tier memory management policy covering global ~/.claude/preferences.md (<=70 lines), project CLAUDE.md (with HYGIENE:AUTO markers and path-scoped .claude/rules/), and transient .claude/handoff.md.
user-invocable: true
---

# Core Memory Architecture

Keep every memory tier strictly within its scope and token budget:

## Tier 1: Global Preferences (`~/.claude/preferences.md`) & Rules (`~/.claude/rules/`)
- Loaded in every session; keep `rules/` ultra-compact and `preferences.md` strictly **<= 70 lines**.
- Consolidated every **10 hours** of cumulative work by isolated background Haiku (merges recurring cross-project patterns and replaces stale lines rather than appending).

## Tier 2: Project Memory (`./CLAUDE.md` & `./.claude/rules/*.md`)
- Written in **English**. Store ONLY: exact build/test/run commands, non-obvious architecture invariants, and legacy traps that reading a single file would not reveal.
- Keep manual notes **above** `<!-- HYGIENE:AUTO -->`. Background Haiku (every **40 minutes** of accumulated project work) updates ONLY the block between `<!-- HYGIENE:AUTO -->` and `<!-- /HYGIENE:AUTO -->` and is automatically rolled back if it touches anything outside.
- Use `.claude/rules/*.md` with YAML `paths:` frontmatter for folder/extension-specific rules (e.g., `paths: ["**/*.razor"]`) instead of bloating root `CLAUDE.md`.

## Tier 3: Session Continuity (`./.claude/handoff.md`)
- Created manually via `/core-wrapup` before `/clear`.
- `SessionStart` injects `.claude/handoff.md` (if < 24h old) into the fresh session via `additionalContext` and archives it to `.claude/handoff.consumed.md`.
- Never dump transient sprint checklists or in-flight debugging state into `CLAUDE.md`.
