---
name: core-subagents
description: Delegation policy for isolating noisy searches, build/test log analysis, and parallel exploration in subagents so the main context window stays clean. Use automatically whenever exploration spans multiple directories or tool outputs are verbose.
user-invocable: true
---

# Core Subagent Delegation

Keep the main context window below the 50% alert threshold by isolating search noise and verbose logs inside subagents.

## Routing Guide
- **Repository mapping / stack detection:** Delegate to `repo-recon` (Haiku, read-only).
- **Multi-file pattern discovery or dependency tracing:** Delegate to `Explore` (Haiku/Sonnet read-only).
- **Verbose failure triage:** When `dotnet test`, `dotnet build`, or Docker logs exceed ~50 lines, summarize or inspect failures in a subagent and return only root cause + `file:line`.
- **Parallel independent investigations:** Spawn concurrent read-only subagents when checking separate boundaries (e.g., EF Core schema vs Blazor UI components).

## When to Work Directly (No Subagent)
- Reading 1–2 known files or running a targeted `Grep` for an exact symbol.
- Implementing tightly coupled edits that rely on immediate main-session state.

## Mandatory Delegation Contract
Every subagent prompt MUST specify:
1. **Exact scope & question** (what to inspect, what to ignore).
2. **Read-only constraint** unless explicitly tasked with an isolated write.
3. **Output ceiling (<= 20 lines):** verified `file_path:line_number` references, concrete findings, and explicit gaps/uncertainties.
