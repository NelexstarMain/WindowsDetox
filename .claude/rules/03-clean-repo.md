# Repository & Context Hygiene

1. **Zero leftovers:** Remove temporary scratch files, debug prints, unused imports, and dead code introduced during the task.
2. **Protect main context:** Delegate broad codebase searches and verbose build/test log analysis to subagents (`repo-recon` / `Explore`).
3. **Keep memory lean:** Keep `./CLAUDE.md` focused on build/test commands and non-obvious invariants; store transient task state in `.claude/handoff.md` via `/core-wrapup`.
4. **Protect secrets:** Never hardcode credentials or paste `.env` values into code, tests, or documentation.
