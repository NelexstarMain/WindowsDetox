#!/usr/bin/env python3
"""PreCompact hook: captures key-moment snapshot into queue.jsonl before context compaction."""

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
    project_root = lh.resolve_project_root(payload.get("cwd"))
    sess_path = lh.session_state_path(session_id)
    state = lh.read_json_safe(sess_path, default={})

    files_touched = list(state.get("files_touched") or [])
    turn_errors = list(state.get("turn_errors") or [])
    intent = str(state.get("prompt_head") or "pre-compact snapshot")
    start_ts = state.get("turn_start_ts")
    duration_s = int(max(0, time.time() - float(start_ts))) if isinstance(start_ts, (int, float)) else 0

    entry = {
        "ts": lh.utc_iso_now(),
        "trigger": "pre_compact",
        "duration_s": duration_s,
        "intent": intent[:160],
        "files": files_touched[:20],
        "errors": turn_errors[:5],
        "tests": str(state.get("last_test_status") or "none"),
        "outcome_tail": "Context compaction triggered; preserving active work summary.",
    }
    queue_path = lh.project_state_dir(project_root) / "queue.jsonl"
    lh.append_jsonl_locked(queue_path, entry)


if __name__ == "__main__":
    main()
