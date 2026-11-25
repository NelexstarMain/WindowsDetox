#!/usr/bin/env python3
"""SubagentStart / SubagentStop async hook: lightweight delegation metrics."""

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
    parser.add_argument("--event", choices=("start", "stop"), default="start")
    args, _ = parser.parse_known_args()

    payload = lh.read_stdin_json()
    session_id = str(payload.get("session_id") or "default")
    agent_type = str(payload.get("agent_type") or "subagent")
    agent_key = str(payload.get("agent_id") or agent_type)
    now = time.time()

    sess_path = lh.session_state_path(session_id)

    def _update(state: dict) -> dict:
        if args.event == "start":
            state["subagents_started"] = int(state.get("subagents_started") or 0) + 1
            active = list(state.get("active_subagents") or [])
            active.append(agent_type)
            state["active_subagents"] = active[-10:]
            lh.busy_open(state, "subagent", agent_key, now)
        else:
            state["subagents_completed"] = int(state.get("subagents_completed") or 0) + 1
            lh.busy_close(state, "subagent", agent_key, now)
        return state

    lh.locked_update_json(sess_path, _update)


if __name__ == "__main__":
    main()
