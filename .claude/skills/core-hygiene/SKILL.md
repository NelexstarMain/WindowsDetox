---
name: core-hygiene
description: Manual repository cleanup and documentation slimming ritual. Run explicitly when reviewing .claude/hygiene-report.md, removing confirmed dead code or scratch files, and trimming bloated CLAUDE.md notes.
disable-model-invocation: true
user-invocable: true
---

# Core Hygiene & Cleanup Ritual

## Live Hygiene Context (Preprocessed)
Git status:
!`git status -sb 2>/dev/null || echo "(no git repo)"`

Current `.claude/hygiene-report.md`:
!`cat .claude/hygiene-report.md 2>/dev/null || echo "(no .claude/hygiene-report.md yet)"`

---

## Cleanup Checklist
Communicate findings and ask confirmation in **Polish**; update documentation in **English**.

1. **Audit reported items:** For every suspected dead file, unused symbol, or stale `CLAUDE.md` entry in `.claude/hygiene-report.md`, verify with `Grep` / `git log` whether it is truly obsolete or active WIP.
2. **Confirm before deleting:** Present a concise list of confirmed scratch/dead files to the user in **Polish** and wait for explicit approval before deleting (`rm`) anything.
3. **Slim `CLAUDE.md`:** Remove obsolete workarounds or instructions duplicated from `~/.claude/rules/` in the manual section of `./CLAUDE.md`. Keep `<!-- HYGIENE:AUTO -->` markers intact.
4. **Archive resolved report items:** Update `.claude/hygiene-report.md` so resolved items are cleared.
5. **Re-verify:** Run the project's test/build suite after any code or config cleanup before finishing.
