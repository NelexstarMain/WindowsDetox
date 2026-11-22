#!/usr/bin/env python3
"""PostToolUse and PostToolUseFailure hook: tracks code edits, test executions, and failures."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402


def extract_file_paths(tool_input: dict) -> list[str]:
    paths: list[str] = []
    for key in ("file_path", "path", "target_file"):
        val = tool_input.get(key)
        if isinstance(val, str) and val.strip():
            paths.append(val.strip())
    return paths


def main() -> None:
    if lh.is_bg_hygiene():
        sys.exit(0)

    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--failure", action="store_true")
    args, _ = parser.parse_known_args()

    payload = lh.read_stdin_json()
    session_id = str(payload.get("session_id") or "default")
    event_name = str(payload.get("hook_event_name") or "")
    is_failure = args.failure or (event_name == "PostToolUseFailure")

    tool_name = str(payload.get("tool_name") or "")
    tool_input = payload.get("tool_input")
    if not isinstance(tool_input, dict):
        tool_input = {}

    project_root = lh.resolve_project_root(payload.get("cwd"))
    sess_path = lh.session_state_path(session_id)

    def _update(state: dict) -> dict:
        state["project_root"] = str(project_root)

        if is_failure:
            err_msg = str(
                payload.get("error")
                or payload.get("tool_response")
                or f"{tool_name} failed"
            ).strip()
            compact_err = " ".join(err_msg.splitlines()[:2])[:200]
            errors = list(state.get("turn_errors") or [])
            if compact_err and compact_err not in errors:
                errors.append(compact_err)
            state["turn_errors"] = errors[-10:]

        if tool_name in ("Edit", "Write", "MultiEdit", "NotebookEdit") and not is_failure:
            touched = list(state.get("files_touched") or [])
            for fp in extract_file_paths(tool_input):
                try:
                    rel = Path(fp).resolve().relative_to(project_root).as_posix()
                except Exception:
                    rel = fp
                if rel not in touched:
                    touched.append(rel)
                if not lh.is_ignored_path(fp, project_root):
                    state["code_dirty"] = True
                    state["tests_ran_since_dirty"] = False
            state["files_touched"] = touched[-30:]

        elif tool_name == "Bash":
            cmd = str(tool_input.get("command") or "")
            if lh.is_test_or_build_command(cmd, project_root):
                state["last_test_cmd"] = cmd[:120]
                state["tests_ran_since_dirty"] = True
                if is_failure:
                    state["last_test_status"] = "fail"
                else:
                    # Check tool_response exitCode/isError if present
                    resp = payload.get("tool_response")
                    failed_resp = False
                    if isinstance(resp, dict):
                        if resp.get("isError") or (
                            isinstance(resp.get("exitCode"), int) and resp["exitCode"] != 0
                        ):
                            failed_resp = True
                    if failed_resp:
                        state["last_test_status"] = "fail"
                    else:
                        state["last_test_status"] = "pass"
                        state["code_dirty"] = False

        return state

    lh.locked_update_json(sess_path, _update)


if __name__ == "__main__":
    main()
