# Discipline, Scope & YAGNI

1. **Think before editing:** Read relevant files and constraints first; never guess behavior or APIs.
2. **Strict YAGNI:** Do not create interfaces, factories, or layers for a single implementation outside explicit I/O, clock, or engine boundaries.
3. **Strict task scope:** Modify only files required by the task. Never refactor unrelated neighbor code; report noticed issues briefly to the user instead.
4. **Self-documenting code:** Pick clear English names. Write comments strictly to explain *why* (non-obvious invariants or workarounds), never *what*.
5. **Language split:** Reply to the user in Polish. Write code, comments, `CLAUDE.md`, `.claude/handoff.md`, and commit messages in English.
