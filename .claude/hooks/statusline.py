#!/usr/bin/env python3
"""Claude Code statusLine script: renders widgets and caches context_window for hooks."""

from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402


def git_branch_summary(project_root: Path) -> str:
    try:
        branch_res = subprocess.run(
            ["git", "-C", str(project_root), "rev-parse", "--abbrev-ref", "HEAD"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        if branch_res.returncode != 0 or not branch_res.stdout.strip():
            return "no-git"
        branch = branch_res.stdout.strip()
        dirty_res = subprocess.run(
            ["git", "-C", str(project_root), "status", "--porcelain"],
            capture_output=True,
            text=True,
            timeout=1.0,
            check=False,
        )
        dirty_mark = "*" if (dirty_res.returncode == 0 and dirty_res.stdout.strip()) else ""
        return f"{branch}{dirty_mark}"
    except Exception:
        return "no-git"


def render_bar(pct: float, width: int = 10) -> str:
    clamped = max(0.0, min(100.0, pct))
    filled = int(round((clamped / 100.0) * width))
    return "#" * filled + "-" * (width - filled)


def colorize(text: str, pct: float) -> str:
    if pct >= lh.ALERT_CONTEXT_PCT:
        return f"\033[31m{text}\033[0m"  # Red >= 50%
    if pct >= lh.WARN_CONTEXT_PCT:
        return f"\033[33m{text}\033[0m"  # Yellow >= 30%
    return f"\033[32m{text}\033[0m"      # Green < 30%


def main() -> None:
    payload = lh.read_stdin_json()
    session_id = str(payload.get("session_id") or "default")
    cwd = (
        (payload.get("workspace") or {}).get("current_dir")
        or payload.get("cwd")
        or None
    )
    project_root = lh.resolve_project_root(cwd)

    model_info = payload.get("model") or {}
    model_name = str(model_info.get("display_name") or model_info.get("id") or "Claude")

    ctx_info = payload.get("context_window") or {}
    raw_pct = ctx_info.get("used_percentage")
    used_pct = float(raw_pct) if isinstance(raw_pct, (int, float)) else 0.0

    cost_info = payload.get("cost") or {}
    raw_cost = cost_info.get("total_cost_usd")
    cost_usd = float(raw_cost) if isinstance(raw_cost, (int, float)) else 0.0

    # Cache context percentage atomically so Stop hook can read exact value
    sess_path = lh.session_state_path(session_id)

    def _update_cache(state: dict) -> dict:
        state["context_used_pct"] = round(used_pct, 1)
        state["context_updated_at"] = time.time()
        state["project_root"] = str(project_root)
        return state

    sess_state = lh.locked_update_json(sess_path, _update_cache)

    # Read project 40-min hygiene timer
    proj_meta_path = lh.project_state_dir(project_root) / "project_meta.json"
    proj_meta = lh.read_json_safe(proj_meta_path, default={})
    acc_min = int(round(float(proj_meta.get("accumulated_s", 0)) / 60.0))

    # Determine test/dirty status widget
    if sess_state.get("code_dirty"):
        if sess_state.get("tests_ran_since_dirty") and sess_state.get("last_test_status") == "fail":
            test_badge = "TESTS:RED"
        else:
            test_badge = "TESTS:DIRTY"
    else:
        last_status = sess_state.get("last_test_status")
        test_badge = "TESTS:FAIL" if last_status == "fail" else "TESTS:OK"

    branch = git_branch_summary(project_root)
    bar = render_bar(used_pct)
    ctx_segment = colorize(f"CTX [{bar}] {used_pct:.0f}%", used_pct)
    if used_pct >= lh.ALERT_CONTEXT_PCT:
        ctx_segment += " (/core-wrapup -> /clear)"

    parts = [
        f"[{model_name}]",
        f"git:{branch}",
        ctx_segment,
        f"HYG:{acc_min}/{lh.PROJECT_HYGIENE_MINUTES}m",
        test_badge,
        f"${cost_usd:.2f}",
    ]
    print(" | ".join(parts), end="")


if __name__ == "__main__":
    main()
