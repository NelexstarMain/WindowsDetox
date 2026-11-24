# cost-meter

A Claude Code mod (hooks-module plugin) that opens a "Cost & hygiene" pane:

- the estimated USD cost of the project's Claude Code sessions at API list prices (total, today, last 7 days,
  per model, main thread vs subagents), read from the transcripts in `~/.claude/projects/<project>/`;
- the progress of the Claude Code Hygiene hooks (`.claude/hooks/`) toward their next background CLAUDE.md refresh
  and global consolidation.

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
```
