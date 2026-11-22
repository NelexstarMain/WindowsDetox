#!/usr/bin/env python3
"""Stop hook: enforces smart TDD gate, logs key moments, checks 50% context, and triggers background Haiku."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402


def spawn_bg_hygiene(mode: str, project_root: Path | None = None) -> None:
    bg_script = Path(__file__).resolve().parent / "bg_hygiene.py"
    if not bg_script.exists():
        return
    cmd = [sys.executable, str(bg_script), "--mode", mode]
    if project_root is not None:
        cmd.extend(["--project-dir", str(project_root)])
    env = os.environ.copy()
    env["HYGIENE_BG"] = "1"
    try:
        subprocess.Popen(
            cmd,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            start_new_session=True,
        )
    except Exception:
        pass


def main() -> None:
    if lh.is_bg_hygiene():
        sys.exit(0)

    payload = lh.read_stdin_json()
    session_id = str(payload.get("session_id") or "default")
    stop_hook_active = bool(payload.get("stop_hook_active"))
    last_msg = str(payload.get("last_assistant_message") or "").strip()
    transcript_path = payload.get("transcript_path")
    project_root = lh.resolve_project_root(payload.get("cwd"))

    sess_path = lh.session_state_path(session_id)
    now_ts = time.time()

    # Atomically read & close turn timing, check smart TDD gate
    gate_decision: dict[str, str] | None = None
    turn_duration_s = 0
    should_count_time = False

    proj_cfg = lh.load_project_hygiene_config(project_root)
    tdd_gate_disabled = bool(proj_cfg.get("disable_tdd_gate", False))

    def _process_stop(state: dict) -> dict:
        nonlocal gate_decision, turn_duration_s, should_count_time

        code_dirty = bool(state.get("code_dirty"))
        tests_ran = bool(state.get("tests_ran_since_dirty"))
        retry_count = int(state.get("stop_retry_count") or 0)

        # Smart TDD Gate:
        # Block only if production code was modified without running ANY test/build afterwards,
        # and respect stop_hook_active + STOP_RETRY_CAP (1).
        if (
            not tdd_gate_disabled
            and code_dirty
            and not tests_ran
            and not stop_hook_active
            and retry_count < lh.STOP_RETRY_CAP
        ):
            state["stop_retry_count"] = retry_count + 1
            gate_decision = {
                "decision": "block",
                "reason": (
                    "Production code was edited without running tests or build verification. "
                    "Run the relevant test/build command (or configure .claude/hygiene.json if this "
                    "repo has custom verification scripts) before concluding. "
                    "(If you are intentionally at a TDD Red phase, run the test so the failure is observed.)"
                ),
            }
            return state

        # Compute turn duration exactly once per turn
        start_ts = state.get("turn_start_ts")
        if state.get("turn_active") and not state.get("turn_counted") and isinstance(start_ts, (int, float)):
            turn_duration_s = int(max(0.0, now_ts - float(start_ts)))
            should_count_time = True
            state["turn_counted"] = True
            state["turn_active"] = False
        return state

    sess_state = lh.locked_update_json(sess_path, _process_stop)

    if gate_decision is not None:
        print(json.dumps(gate_decision, ensure_ascii=False))
        sys.exit(0)

    # Key-Moment Funnel (0 tokens)
    files_touched = list(sess_state.get("files_touched") or [])
    turn_errors = list(sess_state.get("turn_errors") or [])
    last_test_status = str(sess_state.get("last_test_status") or "none")

    triggers: list[str] = []
    if turn_duration_s >= lh.LONG_TURN_SECONDS and (files_touched or turn_errors):
        triggers.append("long_turn_with_changes")
    if turn_errors and last_test_status == "pass":
        triggers.append("error_recovered")
    if len(files_touched) >= 3:
        triggers.append("multi_file_edit")

    proj_dir = lh.project_state_dir(project_root)
    queue_path = proj_dir / "queue.jsonl"

    if should_count_time and triggers:
        tail = " ".join(last_msg.splitlines())[-220:] if last_msg else ""
        entry = {
            "ts": lh.utc_iso_now(),
            "trigger": triggers[0],
            "all_triggers": triggers,
            "duration_s": turn_duration_s,
            "intent": str(sess_state.get("prompt_head") or "")[:160],
            "files": files_touched[:20],
            "errors": turn_errors[:5],
            "tests": f"{last_test_status} ({sess_state.get('last_test_cmd', '')})".strip(),
            "outcome_tail": tail,
        }
        lh.append_jsonl_locked(queue_path, entry)

    # Accumulate work time & evaluate 40m project / 10h global triggers with retry cooldown
    if should_count_time and turn_duration_s > 0:
        proj_meta_path = proj_dir / "project_meta.json"
        trigger_project_bg = False

        def _acc_proj(meta: dict) -> dict:
            nonlocal trigger_project_bg
            meta["project_root"] = str(project_root)
            acc = int(meta.get("accumulated_s") or 0) + turn_duration_s
            meta["accumulated_s"] = acc
            last_try = float(meta.get("last_bg_attempt_ts") or 0.0)
            if (
                acc >= lh.PROJECT_HYGIENE_MINUTES * 60
                and queue_path.exists()
                and queue_path.stat().st_size > 0
                and (now_ts - last_try) >= lh.BG_RETRY_COOLDOWN_SECONDS
            ):
                meta["last_bg_attempt_ts"] = now_ts
                trigger_project_bg = True
            return meta

        lh.locked_update_json(proj_meta_path, _acc_proj)
        if trigger_project_bg:
            spawn_bg_hygiene("project", project_root)

        global_path = lh.global_counters_path()
        trigger_global_bg = False

        def _acc_global(g: dict) -> dict:
            nonlocal trigger_global_bg
            acc = int(g.get("accumulated_s") or 0) + turn_duration_s
            g["accumulated_s"] = acc
            last_try = float(g.get("last_bg_attempt_ts") or 0.0)
            if (
                acc >= lh.GLOBAL_CONSOLIDATION_HOURS * 3600
                and (now_ts - last_try) >= lh.BG_RETRY_COOLDOWN_SECONDS
            ):
                g["last_bg_attempt_ts"] = now_ts
                trigger_global_bg = True
            return g

        lh.locked_update_json(global_path, _acc_global)
        if trigger_global_bg:
            spawn_bg_hygiene("global", project_root)

    # Context Window 50% Alert via zero-token systemMessage
    used_pct = lh.extract_context_pct(
        sess_state, str(transcript_path) if transcript_path else None
    )
    if used_pct is not None and used_pct >= lh.ALERT_CONTEXT_PCT:
        msg = {
            "systemMessage": (
                f"[Hygiene] Context window at {used_pct:.0f}% (>= {lh.ALERT_CONTEXT_PCT:.0f}%). "
                "Finish your current milestone, run /core-wrapup, and reset with /clear."
            )
        }
        print(json.dumps(msg, ensure_ascii=False))


if __name__ == "__main__":
    main()
