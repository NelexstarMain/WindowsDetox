#!/usr/bin/env python3
"""SessionStart hook: resets turn flags and injects + archives .claude/handoff.md on clear/startup."""

from __future__ import annotations

import json
import os
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
    source = str(payload.get("source") or "startup")
    project_root = lh.resolve_project_root(payload.get("cwd"))

    # Reset per-session transient turn/dirty tracking on fresh start or /clear
    if source in ("startup", "clear"):
        sess_path = lh.session_state_path(session_id)

        def _reset(state: dict) -> dict:
            state["session_id"] = session_id
            state["project_root"] = str(project_root)
            state["code_dirty"] = False
            state["tests_ran_since_dirty"] = True
            state["stop_retry_count"] = 0
            state["turn_active"] = False
            state["turn_start_ts"] = None
            state["files_touched"] = []
            state["turn_errors"] = []
            if source == "clear":
                state["context_used_pct"] = 0.0
            return state

        lh.locked_update_json(sess_path, _reset)

    if source not in ("clear", "startup"):
        sys.exit(0)

    handoff_path = project_root / ".claude" / "handoff.md"
    if not handoff_path.exists():
        sys.exit(0)

    try:
        age_s = time.time() - handoff_path.stat().st_mtime
        if age_s > lh.HANDOFF_MAX_AGE_SECONDS:
            sys.exit(0)
        content = handoff_path.read_text(encoding="utf-8").strip()
        if not content:
            sys.exit(0)

        consumed_path = project_root / ".claude" / "handoff.consumed.md"
        os.replace(handoff_path, consumed_path)

        output = {
            "hookSpecificOutput": {
                "hookEventName": "SessionStart",
                "additionalContext": (
                    "Resuming from .claude/handoff.md (archived to .claude/handoff.consumed.md):\n\n"
                    + content
                ),
            }
        }
        print(json.dumps(output, ensure_ascii=False))
    except Exception:
        sys.exit(0)


if __name__ == "__main__":
    main()
