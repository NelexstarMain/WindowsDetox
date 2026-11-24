#!/usr/bin/env python3
"""Prints the USD cost of a project's Claude Code sessions as JSON, from the token usage in its transcripts."""

from __future__ import annotations

import glob
import hashlib
import json
import os
import re
import sys
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


def build_report(root: str) -> dict:
    base = projects_dir(root)
    files = glob.glob(os.path.join(base, "**", "*.jsonl"), recursive=True)
    latest: dict[str, tuple] = {}  # message id -> (model, usage, timestamp, is_subagent); streamed copies repeat an id
    for path in files:
        is_subagent = f"{os.sep}subagents{os.sep}" in path
        with open(path, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if '"usage"' not in line:
                    continue
                try:
                    entry = json.loads(line)
                except ValueError:
                    continue
                msg = entry.get("message")
                if isinstance(msg, dict) and msg.get("id") and isinstance(msg.get("usage"), dict):
                    latest[msg["id"]] = (msg.get("model") or "", msg["usage"], entry.get("timestamp") or "", is_subagent)

    today = datetime.now(timezone.utc).date()
    day_keys = [(today - timedelta(days=n)).isoformat() for n in range(DAYS_SHOWN - 1, -1, -1)]
    per_day = dict.fromkeys(day_keys, 0.0)
    models: dict[str, dict] = defaultdict(lambda: {"cost": 0.0, "input": 0, "output": 0, "cacheWrite": 0, "cacheRead": 0, "messages": 0})
    unpriced: set[str] = set()
    main_cost = sub_cost = 0.0
    for model, usage, stamp, is_subagent in latest.values():
        price = PRICES.get(model)
        if price is None:
            if model and not model.startswith("<"):
                unpriced.add(model)
            continue
        cost = message_cost(price, usage)
        row = models[model]
        row["cost"] += cost
        row["input"] += usage.get("input_tokens") or 0
        row["output"] += usage.get("output_tokens") or 0
        row["cacheWrite"] += usage.get("cache_creation_input_tokens") or 0
        row["cacheRead"] += usage.get("cache_read_input_tokens") or 0
        row["messages"] += 1
        if is_subagent:
            sub_cost += cost
        else:
            main_cost += cost
        if stamp[:10] in per_day:
            per_day[stamp[:10]] += cost

    return {
        "total": round(main_cost + sub_cost, 4),
        "today": round(per_day[day_keys[-1]], 4),
        "mainCost": round(main_cost, 4),
        "subagentCost": round(sub_cost, 4),
        "sessions": sum(1 for f in files if f"{os.sep}subagents{os.sep}" not in f),
        "subagents": sum(1 for f in files if f"{os.sep}subagents{os.sep}" in f),
        "days": [{"date": d, "cost": round(per_day[d], 4)} for d in day_keys],
        "models": sorted(
            ({"model": m, "label": PRICES[m]["label"], **{k: (round(v, 4) if k == "cost" else v) for k, v in r.items()}}
             for m, r in models.items()),
            key=lambda r: -r["cost"],
        ),
        "unpriced": sorted(unpriced),
        "hygiene": hygiene_status(root),
        "updatedAt": datetime.now(timezone.utc).isoformat(timespec="seconds"),
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
