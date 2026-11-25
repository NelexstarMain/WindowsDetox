#!/usr/bin/env python3
"""PreToolUse / PostToolUse / PostToolUseFailure hook: measures how long commands (tool calls) run."""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402


def main() -> None:
    if lh.is_bg_hygiene():
        sys.exit(0)

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--event", choices=("begin", "end"), required=True)
    args, _ = parser.parse_known_args()

    payload = lh.read_stdin_json()
    # A subagent's own tool calls happen inside its subagent interval, which is already counted.
    if payload.get("agent_id"):
        sys.exit(0)

    session_id = str(payload.get("session_id") or "default")
    key = str(payload.get("tool_use_id") or payload.get("tool_name") or "tool")
    now = time.time()

    def _update(state: dict) -> dict:
        if args.event == "begin":
            lh.busy_open(state, "tool", key, now)
        else:
            lh.busy_close(state, "tool", key, now)
        return state

    lh.locked_update_json(lh.session_state_path(session_id), _update)


if __name__ == "__main__":
    main()
