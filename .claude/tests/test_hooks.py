#!/usr/bin/env python3
"""Unit and integration tests for Claude Code Hygiene System hooks."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

TEMPLATE_ROOT = Path(__file__).resolve().parent.parent
HOOKS_DIR = TEMPLATE_ROOT / "hooks"

sys.path.insert(0, str(HOOKS_DIR))
import bg_hygiene  # noqa: E402
import lib_hygiene as lh  # noqa: E402


def run_hook(
    script_name: str,
    payload: dict,
    claude_home: Path,
    args: list[str] | None = None,
    extra_env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    env = os.environ.copy()
    env["CLAUDE_HYGIENE_HOME"] = str(claude_home)
    if extra_env:
        env.update(extra_env)
    cmd = [sys.executable, str(HOOKS_DIR / script_name)] + (args or [])
    return subprocess.run(
        cmd,
        input=json.dumps(payload),
        text=True,
        capture_output=True,
        env=env,
        check=False,
    )


class HygieneHooksTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.claude_home = self.root / "claude_home"
        self.project_dir = self.root / "project"
        self.claude_home.mkdir(parents=True)
        self.project_dir.mkdir(parents=True)
        (self.claude_home / "hygiene").mkdir(parents=True)
        (self.claude_home / "hygiene" / "haiku-project-settings.json").write_text(
            (TEMPLATE_ROOT / "hygiene" / "haiku-project-settings.json").read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        (self.claude_home / "hygiene" / "haiku-global-settings.json").write_text(
            (TEMPLATE_ROOT / "hygiene" / "haiku-global-settings.json").read_text(encoding="utf-8"),
            encoding="utf-8",
        )
        (self.claude_home / "preferences.md").write_text(
            (TEMPLATE_ROOT / "preferences.md").read_text(encoding="utf-8"),
            encoding="utf-8",
        )

    def tearDown(self) -> None:
        self.tmp.cleanup()

    def test_recursion_guard_exits_cleanly_when_hygiene_bg_set(self) -> None:
        res = run_hook(
            "stop_guard.py",
            {"session_id": "s1", "cwd": str(self.project_dir)},
            self.claude_home,
            extra_env={"HYGIENE_BG": "1"},
        )
        self.assertEqual(res.returncode, 0)
        self.assertEqual(res.stdout.strip(), "")

    def test_ignored_paths_and_test_command_detection(self) -> None:
        self.assertTrue(lh.is_ignored_path(str(self.project_dir / "README.md"), self.project_dir))
        self.assertTrue(lh.is_ignored_path(str(self.project_dir / ".claude" / "handoff.md"), self.project_dir))
        self.assertTrue(lh.is_ignored_path(str(self.project_dir / "docs" / "notes.txt"), self.project_dir))
        self.assertTrue(lh.is_ignored_path(str(self.project_dir / "assets" / "logo.png"), self.project_dir))
        self.assertFalse(lh.is_ignored_path(str(self.project_dir / "src" / "OrderService.cs"), self.project_dir))

        self.assertTrue(lh.is_test_or_build_command("dotnet test --no-build"))
        self.assertTrue(lh.is_test_or_build_command("cd api && uv run pytest -q"))
        self.assertTrue(lh.is_test_or_build_command("docker compose exec api pytest"))
        self.assertTrue(lh.is_test_or_build_command("make test"))
        self.assertTrue(lh.is_test_or_build_command("cargo test"))
        self.assertFalse(lh.is_test_or_build_command("git status"))
        self.assertFalse(lh.is_test_or_build_command('git commit -m "dotnet test"'))

        # Per-project .claude/hygiene.json overrides
        (self.project_dir / ".claude").mkdir(exist_ok=True)
        (self.project_dir / ".claude" / "hygiene.json").write_text(
            json.dumps({
                "extra_test_commands": ["./scripts/verify.sh"],
                "extra_ignored_paths": ["sql/seeds/**"],
            }),
            encoding="utf-8",
        )
        self.assertTrue(lh.is_test_or_build_command("./scripts/verify.sh --fast", self.project_dir))
        self.assertTrue(lh.is_ignored_path(str(self.project_dir / "sql" / "seeds" / "init.sql"), self.project_dir))

    def test_smart_tdd_gate_blocks_dirty_code_then_allows_conscious_red_and_stop_hook_active(self) -> None:
        sid = "tdd-session"
        run_hook("prompt_submit.py", {"session_id": sid, "cwd": str(self.project_dir), "prompt": "Implement tax calc"}, self.claude_home)

        # Editing README.md must NOT trigger dirty code
        run_hook(
            "post_tool.py",
            {"session_id": sid, "cwd": str(self.project_dir), "tool_name": "Edit", "tool_input": {"file_path": str(self.project_dir / "README.md")}},
            self.claude_home,
        )
        res_md = run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)
        self.assertEqual(res_md.stdout.strip(), "")

        # Editing C# file triggers dirty code -> Stop blocks once
        run_hook("prompt_submit.py", {"session_id": sid, "cwd": str(self.project_dir), "prompt": "Add TaxCalculator"}, self.claude_home)
        run_hook(
            "post_tool.py",
            {"session_id": sid, "cwd": str(self.project_dir), "tool_name": "Write", "tool_input": {"file_path": str(self.project_dir / "TaxCalculator.cs")}},
            self.claude_home,
        )
        res_block = run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)
        decision = json.loads(res_block.stdout)
        self.assertEqual(decision["decision"], "block")

        # Conscious TDD Red: test ran and failed -> Stop MUST allow without blocking
        run_hook(
            "post_tool.py",
            {
                "session_id": sid,
                "cwd": str(self.project_dir),
                "tool_name": "Bash",
                "tool_input": {"command": "dotnet test"},
                "error": "1 test failed",
            },
            self.claude_home,
            args=["--failure"],
        )
        res_red = run_hook(
            "stop_guard.py",
            {"session_id": sid, "cwd": str(self.project_dir), "stop_hook_active": False},
            self.claude_home,
        )
        self.assertEqual(res_red.stdout.strip(), "")

    def test_subagent_edits_do_not_dirty_the_parent_session(self) -> None:
        sid = "parent-session"
        run_hook("prompt_submit.py", {"session_id": sid, "cwd": str(self.project_dir), "prompt": "Delegate work"}, self.claude_home)
        run_hook(
            "post_tool.py",
            {
                "session_id": sid,
                "agent_id": "a123",
                "cwd": str(self.project_dir),
                "tool_name": "Edit",
                "tool_input": {"file_path": str(self.project_dir / "Worker.cs")},
            },
            self.claude_home,
        )
        res = run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)
        self.assertEqual(res.stdout.strip(), "")

    def test_statusline_caches_context_and_stop_emits_50pct_alert(self) -> None:
        sid = "ctx-session"
        sl_payload = {
            "session_id": sid,
            "cwd": str(self.project_dir),
            "model": {"display_name": "Sonnet 4.5"},
            "context_window": {"used_percentage": 52.4},
            "cost": {"total_cost_usd": 0.42},
        }
        sl_res = run_hook("statusline.py", sl_payload, self.claude_home)
        self.assertEqual(sl_res.returncode, 0)
        self.assertIn("52%", sl_res.stdout)
        self.assertIn("/core-wrapup -> /clear", sl_res.stdout)
        self.assertIn("$0.42", sl_res.stdout)

        stop_res = run_hook(
            "stop_guard.py",
            {"session_id": sid, "cwd": str(self.project_dir)},
            self.claude_home,
        )
        out = json.loads(stop_res.stdout)
        self.assertIn("systemMessage", out)
        self.assertIn("52%", out["systemMessage"])

    def test_session_start_injects_and_archives_handoff_on_clear(self) -> None:
        handoff = self.project_dir / ".claude" / "handoff.md"
        handoff.parent.mkdir(parents=True)
        handoff.write_text("# Session Handoff\n\n## 7. Exact Next Step\nImplement OrderValidator.", encoding="utf-8")

        res = run_hook(
            "session_start.py",
            {"session_id": "s-clear", "source": "clear", "cwd": str(self.project_dir)},
            self.claude_home,
        )
        self.assertEqual(res.returncode, 0)
        out = json.loads(res.stdout)
        self.assertIn("Implement OrderValidator.", out["hookSpecificOutput"]["additionalContext"])
        self.assertFalse(handoff.exists())
        self.assertTrue((self.project_dir / ".claude" / "handoff.consumed.md").exists())

    def test_bg_hygiene_applies_valid_auto_section_and_rolls_back_outside_edits(self) -> None:
        os.environ["CLAUDE_HYGIENE_HOME"] = str(self.claude_home)
        claude_md = self.project_dir / "CLAUDE.md"
        claude_md.write_text(
            "# My Repo\n\n## Manual Invariants\n- Keep manual rules untouched.\n\n"
            "<!-- HYGIENE:AUTO -->\n- Old auto note\n<!-- /HYGIENE:AUTO -->\n",
            encoding="utf-8",
        )
        proj_dir = lh.project_state_dir(self.project_dir.resolve())
        lh.append_jsonl_locked(
            proj_dir / "queue.jsonl",
            {
                "ts": "2026-11-21T08:00:00Z",
                "trigger": "error_recovered",
                "duration_s": 210,
                "intent": "Fix EF Core tracking",
                "files": ["Data/AppDb.cs"],
                "errors": ["InvalidOperationException"],
                "tests": "pass (dotnet test)",
                "outcome_tail": "Added AsNoTracking",
            },
        )

        # Mock CLI that tries to tamper outside HYGIENE:AUTO -> MUST roll back
        bad_cli = self.root / "mock_bad_claude.py"
        bad_cli.write_text(
            "#!/usr/bin/env python3\n"
            "import pathlib\n"
            "p = pathlib.Path('CLAUDE.md')\n"
            "p.write_text('# WIPED MANUAL RULES\\n<!-- HYGIENE:AUTO -->\\n- new\\n<!-- /HYGIENE:AUTO -->\\n')\n",
            encoding="utf-8",
        )
        bad_cli.chmod(0o755)
        os.environ["CLAUDE_HYGIENE_CLI"] = str(bad_cli)
        try:
            status_bad = bg_hygiene.run_project_hygiene(self.project_dir.resolve())
        finally:
            os.environ.pop("CLAUDE_HYGIENE_CLI", None)

        self.assertTrue(status_bad.startswith("rolled_back:"))
        self.assertIn("Keep manual rules untouched.", claude_md.read_text(encoding="utf-8"))

        # Mock CLI that ONLY updates inside HYGIENE:AUTO -> MUST apply & clear queue
        good_cli = self.root / "mock_good_claude.py"
        good_cli.write_text(
            "#!/usr/bin/env python3\n"
            "import pathlib\n"
            "p = pathlib.Path('CLAUDE.md')\n"
            "txt = p.read_text()\n"
            "txt = txt.replace('- Old auto note', '- Use AsNoTracking on read queries')\n"
            "p.write_text(txt)\n",
            encoding="utf-8",
        )
        good_cli.chmod(0o755)
        os.environ["CLAUDE_HYGIENE_CLI"] = str(good_cli)
        try:
            status_good = bg_hygiene.run_project_hygiene(self.project_dir.resolve())
        finally:
            os.environ.pop("CLAUDE_HYGIENE_CLI", None)

        self.assertEqual(status_good, "applied")
        updated = claude_md.read_text(encoding="utf-8")
        self.assertIn("Use AsNoTracking on read queries", updated)
        self.assertIn("Keep manual rules untouched.", updated)
        self.assertEqual((proj_dir / "queue.jsonl").read_text(encoding="utf-8"), "")

    def test_disable_tdd_gate_override_via_project_hygiene_json(self) -> None:
        sid = "docs-repo-session"
        (self.project_dir / ".claude").mkdir(exist_ok=True)
        (self.project_dir / ".claude" / "hygiene.json").write_text(
            json.dumps({"disable_tdd_gate": True}),
            encoding="utf-8",
        )
        run_hook("prompt_submit.py", {"session_id": sid, "cwd": str(self.project_dir), "prompt": "Edit SQL script"}, self.claude_home)
        run_hook(
            "post_tool.py",
            {"session_id": sid, "cwd": str(self.project_dir), "tool_name": "Write", "tool_input": {"file_path": str(self.project_dir / "init.sql")}},
            self.claude_home,
        )
        res = run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)
        self.assertEqual(res.stdout.strip(), "")

    def test_bg_hygiene_in_real_git_repo_with_untracked_claude_dir(self) -> None:
        os.environ["CLAUDE_HYGIENE_HOME"] = str(self.claude_home)
        subprocess.run(["git", "init", str(self.project_dir)], check=True, capture_output=True)
        subprocess.run(["git", "-C", str(self.project_dir), "config", "user.email", "test@example.com"], check=True, capture_output=True)
        subprocess.run(["git", "-C", str(self.project_dir), "config", "user.name", "Test"], check=True, capture_output=True)

        claude_md = self.project_dir / "CLAUDE.md"
        claude_md.write_text(
            "# Git Repo\n\n## Manual\n- Keep intact.\n\n"
            "<!-- HYGIENE:AUTO -->\n- Old\n<!-- /HYGIENE:AUTO -->\n",
            encoding="utf-8",
        )
        subprocess.run(["git", "-C", str(self.project_dir), "add", "CLAUDE.md"], check=True, capture_output=True)
        subprocess.run(["git", "-C", str(self.project_dir), "commit", "-m", "init"], check=True, capture_output=True)

        proj_dir = lh.project_state_dir(self.project_dir.resolve())
        lh.append_jsonl_locked(
            proj_dir / "queue.jsonl",
            {
                "ts": "2026-11-21T08:00:00Z",
                "trigger": "multi_file_edit",
                "duration_s": 190,
                "intent": "Update docs",
                "files": ["CLAUDE.md"],
                "errors": [],
                "tests": "pass (pytest)",
                "outcome_tail": "Done",
            },
        )

        # Mock CLI edits CLAUDE.md inside markers AND .claude/hygiene-report.md (initially untracked .claude/)
        good_git_cli = self.root / "mock_git_good.py"
        good_git_cli.write_text(
            "#!/usr/bin/env python3\n"
            "import pathlib\n"
            "p = pathlib.Path('CLAUDE.md')\n"
            "p.write_text(p.read_text().replace('- Old', '- Verified pytest command'))\n"
            "r = pathlib.Path('.claude/hygiene-report.md')\n"
            "r.write_text('# Hygiene Report\\n\\n- All clean.\\n')\n",
            encoding="utf-8",
        )
        good_git_cli.chmod(0o755)
        os.environ["CLAUDE_HYGIENE_CLI"] = str(good_git_cli)
        try:
            status = bg_hygiene.run_project_hygiene(self.project_dir.resolve())
        finally:
            os.environ.pop("CLAUDE_HYGIENE_CLI", None)

        self.assertEqual(status, "applied")
        self.assertIn("Verified pytest command", claude_md.read_text(encoding="utf-8"))

    def test_busy_clock_counts_overlapping_work_once(self) -> None:
        state: dict = {}
        lh.busy_open(state, "tool", "t1", 1000.0)
        lh.busy_open(state, "subagent", "a1", 1010.0)
        lh.busy_close(state, "tool", "t1", 1020.0)
        self.assertEqual(state.get("unflushed_busy_s", 0), 0)  # subagent still running
        lh.busy_close(state, "subagent", "a1", 1050.0)
        self.assertEqual(state["unflushed_busy_s"], 50)

    def test_busy_clock_ignores_unmatched_close(self) -> None:
        state: dict = {}
        lh.busy_close(state, "tool", "never-opened", 1000.0)
        self.assertEqual(state.get("unflushed_busy_s", 0), 0)

    def test_busy_clock_caps_an_entry_whose_end_event_never_arrived(self) -> None:
        state: dict = {}
        lh.busy_open(state, "tool", "denied", 0.0)  # no matching close, e.g. a permission-denied call
        later = 100_000.0
        lh.busy_open(state, "tool", "t2", later)
        lh.busy_close(state, "tool", "t2", later + 5)
        self.assertEqual(state["unflushed_busy_s"], lh.BUSY_TTL_SECONDS["tool"] + 5)

    def _seed_session(self, sid: str, state: dict) -> None:
        path = self.claude_home / "state" / "sessions" / f"{sid}.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(state), encoding="utf-8")

    def _accumulated(self) -> tuple[int, int]:
        meta = self.claude_home / "state" / "projects" / lh.project_id_for(self.project_dir.resolve()) / "project_meta.json"
        project = json.loads(meta.read_text(encoding="utf-8"))["accumulated_s"]
        glob = json.loads((self.claude_home / "state" / "global_counters.json").read_text(encoding="utf-8"))["accumulated_s"]
        return project, glob

    def test_background_subagent_time_is_counted_without_a_user_prompt_turn(self) -> None:
        import time

        sid = "bg-subagent"
        started = time.time() - 2700
        self._seed_session(sid, {"busy_open": {"subagent:a1": started}, "busy_since_ts": started})
        payload = {"session_id": sid, "cwd": str(self.project_dir), "agent_id": "a1", "agent_type": "general-purpose"}
        run_hook("subagent_track.py", payload, self.claude_home, args=["--event", "stop"])
        # The turn that ends here was started by a notification, so it has no UserPromptSubmit.
        run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)

        project, glob = self._accumulated()
        self.assertGreaterEqual(project, 2699)
        self.assertGreaterEqual(glob, 2699)

    def test_command_time_is_counted_from_pre_and_post_tool_hooks(self) -> None:
        import time

        sid = "tool-time"
        started = time.time() - 600
        self._seed_session(sid, {"busy_open": {"tool:t1": started}, "busy_since_ts": started})
        payload = {"session_id": sid, "cwd": str(self.project_dir), "tool_use_id": "t1", "tool_name": "Bash"}
        run_hook("busy_track.py", payload, self.claude_home, args=["--event", "end"])
        run_hook("stop_guard.py", {"session_id": sid, "cwd": str(self.project_dir)}, self.claude_home)

        project, _ = self._accumulated()
        self.assertGreaterEqual(project, 599)

    def test_prompt_turn_with_tool_inside_is_not_double_counted(self) -> None:
        import time

        sid = "no-double-count"
        turn_start = time.time() - 300
        self._seed_session(sid, {
            "turn_active": True, "turn_counted": False, "turn_start_ts": turn_start,
            "busy_open": {"turn:main": turn_start, "tool:t1": time.time() - 200},
            "busy_since_ts": turn_start,
        })
        base = {"session_id": sid, "cwd": str(self.project_dir)}
        run_hook("busy_track.py", {**base, "tool_use_id": "t1", "tool_name": "Bash"}, self.claude_home, args=["--event", "end"])
        run_hook("stop_guard.py", base, self.claude_home)

        project, _ = self._accumulated()
        self.assertTrue(299 <= project <= 330, project)

    def test_subagent_own_tool_calls_do_not_open_a_second_interval(self) -> None:
        sid = "agent-tools"
        run_hook(
            "busy_track.py",
            {"session_id": sid, "cwd": str(self.project_dir), "agent_id": "a1", "tool_use_id": "t9", "tool_name": "Bash"},
            self.claude_home,
            args=["--event", "begin"],
        )
        state = json.loads((self.claude_home / "state" / "sessions" / f"{sid}.json").read_text(encoding="utf-8")) \
            if (self.claude_home / "state" / "sessions" / f"{sid}.json").exists() else {}
        self.assertEqual(state.get("busy_open", {}), {})


if __name__ == "__main__":
    unittest.main()
