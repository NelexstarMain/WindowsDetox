#!/usr/bin/env python3
"""Prints the USD cost of every Claude Code session of a repository as JSON.

Sessions on this machine are read from their transcripts. Sessions from other machines come from the ledger,
.claude/cost-ledger/ in the repository (one file per session), which this script keeps up to date and git carries between machines."""

from __future__ import annotations

import glob
import hashlib
import json
import os
import re
import sys
import time
from collections import defaultdict
from datetime import datetime, timedelta, timezone

# USD per million tokens, Anthropic first-party API list prices. Cache writes: 1.25x input (5-minute TTL),
# 2x input (1-hour TTL). Haiku 5.5's cache-read price is not published here, so it takes the usual 0.1x.
PRICES = {
    "claude-opus-5-5": {"label": "Opus 5.5", "in": 4.00, "out": 20.00, "read": 0.20},
    "claude-sonnet-5-5": {"label": "Sonnet 5.5", "in": 2.00, "out": 10.00, "read": 0.10},
    "claude-haiku-5-5": {"label": "Haiku 5.5", "in": 0.10, "out": 0.50, "read": 0.01},
}
DAYS_SHOWN = 7
LEDGER_VERSION = 1
LEDGER_STEP_USD = 1.0
IDLE_SECONDS = 600


def projects_dir(project_root: str) -> str:
    return os.path.join(os.path.expanduser("~/.claude/projects"), re.sub(r"[^A-Za-z0-9]", "-", project_root))


def message_cost(price: dict, usage: dict) -> float:
    creation = usage.get("cache_creation") or {}
    write_1h = creation.get("ephemeral_1h_input_tokens") or 0
    write_5m = creation.get("ephemeral_5m_input_tokens")
    if write_5m is None:  # no TTL breakdown: assume the default 5-minute TTL
        write_5m = (usage.get("cache_creation_input_tokens") or 0) - write_1h
    return (
        (usage.get("input_tokens") or 0) * price["in"]
        + (usage.get("output_tokens") or 0) * price["out"]
        + write_5m * price["in"] * 1.25
        + write_1h * price["in"] * 2.0
        + (usage.get("cache_read_input_tokens") or 0) * price["read"]
    ) / 1_000_000


def parse_ts(stamp: str) -> float:
    try:
        return datetime.fromisoformat(stamp.replace("Z", "+00:00")).timestamp()
    except ValueError:
        return 0.0


def new_session(day_cost: dict | None = None) -> dict:
    return {"cost": 0.0, "mainCost": 0.0, "subagentCost": 0.0, "subagents": 0, "messages": 0,
            "lastActivity": "", "days": day_cost or {}, "models": {}}


def scan_sessions(root: str) -> tuple[dict[str, dict], set[str]]:
    """Costs per session from the transcripts on this machine, plus the models that have no price."""
    base = projects_dir(root)
    occurrences: dict[str, tuple] = {}  # message id -> (session, model, usage, timestamp, is_subagent)
    first_seen: dict[str, float] = {}
    subagent_files: dict[str, int] = defaultdict(int)
    for path in sorted(glob.glob(os.path.join(base, "**", "*.jsonl"), recursive=True)):
        parts = os.path.relpath(path, base).split(os.sep)
        is_subagent = len(parts) > 1
        session = parts[0] if is_subagent else parts[0][: -len(".jsonl")]
        subagent_files[session] += is_subagent
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if '"usage"' not in line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                msg = entry.get("message")
                if not (isinstance(msg, dict) and msg.get("id") and isinstance(msg.get("usage"), dict)):
                    continue
                stamp = entry.get("timestamp") or ""
                first_seen[session] = min(first_seen.get(session, float("inf")), parse_ts(stamp))
                held = occurrences.get(msg["id"])
                # A resumed session can hold a copy of an earlier one's messages: the earliest session owns them.
                # Within one session the last copy of a streamed message carries its final usage.
                if held is None or held[0] == session or first_seen[session] < first_seen[held[0]]:
                    occurrences[msg["id"]] = (session, msg.get("model") or "", msg["usage"], stamp, is_subagent)

    sessions: dict[str, dict] = {}
    unpriced: set[str] = set()
    for session, model, usage, stamp, is_subagent in occurrences.values():
        price = PRICES.get(model)
        if price is None:
            if model and not model.startswith("<"):
                unpriced.add(model)
            continue
        entry = sessions.setdefault(session, new_session())
        cost = message_cost(price, usage)
        entry["cost"] += cost
        entry["subagentCost" if is_subagent else "mainCost"] += cost
        entry["messages"] += 1
        entry["lastActivity"] = max(entry["lastActivity"], stamp, key=parse_ts)
        if stamp[:10]:
            entry["days"][stamp[:10]] = entry["days"].get(stamp[:10], 0.0) + cost
        row = entry["models"].setdefault(model, {"cost": 0.0, "input": 0, "output": 0, "cacheWrite": 0, "cacheRead": 0, "messages": 0})
        row["cost"] += cost
        row["input"] += usage.get("input_tokens") or 0
        row["output"] += usage.get("output_tokens") or 0
        row["cacheWrite"] += usage.get("cache_creation_input_tokens") or 0
        row["cacheRead"] += usage.get("cache_read_input_tokens") or 0
        row["messages"] += 1
    for session, entry in sessions.items():
        entry["subagents"] = subagent_files[session]
        for key in ("cost", "mainCost", "subagentCost"):
            entry[key] = round(entry[key], 4)
        entry["days"] = {day: round(value, 4) for day, value in sorted(entry["days"].items())}
        for row in entry["models"].values():
            row["cost"] = round(row["cost"], 4)
    return sessions, unpriced


def ledger_dir(root: str) -> str:
    return os.path.join(root, ".claude", "cost-ledger")


def read_ledger(root: str) -> dict[str, dict]:
    """One file per session, so two machines never edit the same file and git has nothing to conflict on."""
    sessions = {}
    for path in glob.glob(os.path.join(ledger_dir(root), "*.json")):
        entry = _read_json(path)
        if entry:
            sessions[os.path.basename(path)[: -len(".json")]] = entry
    return sessions


def merge_sessions(stored: dict[str, dict], local: dict[str, dict]) -> dict[str, dict]:
    """Every session either side knows. A local copy wins unless it has lost messages the ledger still holds."""
    merged = dict(stored)
    for session, entry in local.items():
        old = stored.get(session)
        if old is None or entry["messages"] >= old.get("messages", 0):
            merged[session] = entry
    return merged


def sessions_to_store(stored: dict[str, dict], merged: dict[str, dict], now: float) -> dict[str, dict]:
    """What the ledger file should hold. The file is tracked by git, so an active session is rewritten only after
    LEDGER_STEP_USD of drift; a session that has been quiet for IDLE_SECONDS is written in full."""
    result = dict(stored)
    for session, entry in merged.items():
        old = stored.get(session)
        if old is None:
            result[session] = entry
        elif entry != old:
            is_idle = now - parse_ts(entry.get("lastActivity", "")) > IDLE_SECONDS
            if is_idle or abs(entry["cost"] - old["cost"]) >= LEDGER_STEP_USD:
                result[session] = entry
    return result


def write_ledger(root: str, stored: dict[str, dict], to_store: dict[str, dict]) -> None:
    for session, entry in to_store.items():
        if stored.get(session) == entry:
            continue
        path = os.path.join(ledger_dir(root), f"{session}.json")
        try:
            os.makedirs(ledger_dir(root), exist_ok=True)
            with open(f"{path}.tmp", "w", encoding="utf-8") as fh:
                json.dump(entry, fh, indent=1, sort_keys=True)
                fh.write("\n")
            os.replace(f"{path}.tmp", path)
        except OSError:
            pass  # a read-only checkout still gets its report


def build_report(root: str, now: float | None = None) -> dict:
    now = time.time() if now is None else now
    local, unpriced = scan_sessions(root)
    stored = read_ledger(root)
    merged = merge_sessions(stored, local)
    to_store = sessions_to_store(stored, merged, now)
    write_ledger(root, stored, to_store)

    today = datetime.fromtimestamp(now, timezone.utc).date()
    day_keys = [(today - timedelta(days=n)).isoformat() for n in range(DAYS_SHOWN - 1, -1, -1)]
    per_day = dict.fromkeys(day_keys, 0.0)
    models: dict[str, dict] = {}
    for entry in merged.values():
        for day, value in entry.get("days", {}).items():
            if day in per_day:
                per_day[day] += value
        for model, row in entry.get("models", {}).items():
            total_row = models.setdefault(model, {"cost": 0.0, "input": 0, "output": 0, "cacheWrite": 0, "cacheRead": 0, "messages": 0})
            for key in total_row:
                total_row[key] += row.get(key, 0)

    return {
        "total": round(sum(e["cost"] for e in merged.values()), 4),
        "today": round(per_day[day_keys[-1]], 4),
        "mainCost": round(sum(e["mainCost"] for e in merged.values()), 4),
        "subagentCost": round(sum(e["subagentCost"] for e in merged.values()), 4),
        "sessions": len(merged),
        "subagents": sum(e.get("subagents", 0) for e in merged.values()),
        "days": [{"date": d, "cost": round(per_day[d], 4)} for d in day_keys],
        "models": sorted(
            ({"model": m, "label": PRICES.get(m, {}).get("label", m), **{k: (round(v, 4) if k == "cost" else v) for k, v in r.items()}}
             for m, r in models.items()),
            key=lambda r: -r["cost"],
        ),
        "unpriced": sorted(unpriced),
        "hygiene": hygiene_status(root),
        "updatedAt": datetime.fromtimestamp(now, timezone.utc).isoformat(timespec="seconds"),
    }


def _read_constant(source: str, name: str) -> float | None:
    found = re.search(rf"^{name}\s*=\s*([0-9_.]+)", source, re.MULTILINE)
    return float(found.group(1).replace("_", "")) if found else None


def _read_json(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def hygiene_status(root: str) -> dict | None:
    """Progress of the project's Claude Code Hygiene hooks toward their next background run, or None without them."""
    lib = os.path.join(root, ".claude", "hooks", "lib_hygiene.py")
    try:
        with open(lib, encoding="utf-8") as fh:
            source = fh.read()
    except OSError:
        return None
    # The hooks key their state by the git top-level path, which is the session root here.
    state_root = os.path.join(os.environ.get("CLAUDE_HYGIENE_HOME") or os.path.expanduser("~/.claude"), "state")
    project_dir = os.path.join(state_root, "projects", hashlib.sha1(root.encode("utf-8")).hexdigest()[:12])
    meta = _read_json(os.path.join(project_dir, "project_meta.json"))
    global_counters = _read_json(os.path.join(state_root, "global_counters.json"))

    queue_events = 0
    try:
        with open(os.path.join(project_dir, "queue.jsonl"), encoding="utf-8") as fh:
            queue_events = sum(1 for line in fh if line.strip())
    except OSError:
        pass

    last_status = None
    try:
        with open(os.path.join(project_dir, "hygiene-audit.jsonl"), encoding="utf-8") as fh:
            lines = [line for line in fh if line.strip()]
        if lines:
            last_status = json.loads(lines[-1]).get("status")
    except (OSError, ValueError):
        pass

    project_minutes = _read_constant(source, "PROJECT_HYGIENE_MINUTES") or 40
    global_hours = _read_constant(source, "GLOBAL_CONSOLIDATION_HOURS") or 10
    return {
        "projectSeconds": int(meta.get("accumulated_s") or 0),
        "projectThresholdSeconds": int(project_minutes * 60),
        "globalSeconds": int(global_counters.get("accumulated_s") or 0),
        "globalThresholdSeconds": int(global_hours * 3600),
        "queueEvents": queue_events,
        "lastRunAt": meta.get("last_hygiene_at"),
        "lastStatus": last_status,
    }


if __name__ == "__main__":
    print(json.dumps(build_report(sys.argv[1] if len(sys.argv) > 1 else os.getcwd())))
