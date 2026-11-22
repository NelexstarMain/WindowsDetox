#!/usr/bin/env python3
"""UserPromptSubmit hook: marks turn start time and captures compact user intent."""

from __future__ import annotations

import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402


def main() -> None:
    if lh.is_bg_hygiene():
        sys.exit(0)

    payload = lh.read_stdin_json()
    session_id = str(payload.get("session_id") or "default")
    prompt_text = str(payload.get("prompt") or "").strip().replace("\n", " ")
    project_root = lh.resolve_project_root(payload.get("cwd"))

    sess_path = lh.session_state_path(session_id)

    def _on_prompt(state: dict) -> dict:
        state["session_id"] = session_id
        state["project_root"] = str(project_root)
        state["turn_start_ts"] = time.time()
        state["turn_active"] = True
        state["turn_counted"] = False
        state["stop_retry_count"] = 0
        state["prompt_head"] = prompt_text[:160]
        state["files_touched"] = []
        state["turn_errors"] = []
        return state

    lh.locked_update_json(sess_path, _on_prompt)


if __name__ == "__main__":
    main()
