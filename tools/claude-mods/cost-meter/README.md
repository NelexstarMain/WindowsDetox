# cost-meter

A Claude Code mod (hooks-module plugin) that opens a "Cost & hygiene" pane:

- the estimated USD cost of every Claude Code session of the repository at API list prices (total, today, last
  7 days, per model, main thread vs subagents);
- the progress of the Claude Code Hygiene hooks (`.claude/hooks/`) toward their next background CLAUDE.md refresh
  and global consolidation.

Sessions on the current machine are read from the transcripts in `~/.claude/projects/<project>/`. A cloud session
starts with no history, so the mod also keeps a **ledger**: one JSON file per session in `.claude/cost-ledger/`.
Commit and push those files and every other machine and session adds them to its total. A session's file is
rewritten when it has been idle for 10 minutes, or after $1 of drift while it is active, so the tree is not dirty
on every refresh. One file per session means two machines never edit the same file.

The desktop and mobile apps draw it as one card; the terminal draws the same figures as text. `/project-cost`
reopens the pane. Figures are client-side estimates; Anthropic's billing is authoritative.

## Install

The repository's `.claude/settings.json` declares the `windowsdetox-tools` marketplace and enables the plugin, so
sessions of this project offer to install it. To install it by hand, at the prompt of a terminal session:

```
/plugin install cost-meter@windowsdetox-tools
```

The repository is private: a machine needs a git credential for github.com to fetch it.

## Develop

```bash
claude plugin validate tools/claude-mods/cost-meter
claude plugin test tools/claude-mods/cost-meter
python3 -m unittest discover -s tools/claude-mods/cost-meter/hooks
```
