#!/usr/bin/env python3
"""Tests for cost.py: the per-session ledger that makes the total cover every session of the repository."""

from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import cost  # noqa: E402

NOW = 1_800_000_000.0  # 2027-01-15 08:00:00 UTC
IDLE = NOW - 3600
ACTIVE = NOW - 30


def iso(ts: float) -> str:
    from datetime import datetime, timezone

    return datetime.fromtimestamp(ts, timezone.utc).isoformat(timespec="seconds")


def message(msg_id: str, ts: float, input_tokens: int = 1_000_000, output_tokens: int = 100_000) -> str:
    """One transcript line: Sonnet 5.5 at $2 / $10 per million, so 1M in + 100k out costs $3.00."""
    return json.dumps({
        "timestamp": iso(ts),
        "message": {"id": msg_id, "model": "claude-sonnet-5-5", "usage": {"input_tokens": input_tokens, "output_tokens": output_tokens}},
    })


def ledger_entry(cost_usd: float, messages: int, last_activity: float = IDLE) -> dict:
    day = iso(last_activity)[:10]
    return {
        "cost": cost_usd, "mainCost": cost_usd, "subagentCost": 0.0, "subagents": 0, "messages": messages,
        "lastActivity": iso(last_activity), "days": {day: cost_usd},
        "models": {"claude-sonnet-5-5": {"cost": cost_usd, "input": 0, "output": 0, "cacheWrite": 0, "cacheRead": 0, "messages": messages}},
    }


class LedgerTest(unittest.TestCase):
    def setUp(self) -> None:
        self.tmp = tempfile.TemporaryDirectory()
        self.home = Path(self.tmp.name) / "home"
        self.root = Path(self.tmp.name) / "repo"
        (self.root / ".claude").mkdir(parents=True)
        self.transcripts = self.home / ".claude" / "projects" / cost.re.sub(r"[^A-Za-z0-9]", "-", str(self.root))
        self.transcripts.mkdir(parents=True)
        self._home = os.environ.get("HOME")
        os.environ["HOME"] = str(self.home)

    def tearDown(self) -> None:
        if self._home is None:
            os.environ.pop("HOME", None)
        else:
            os.environ["HOME"] = self._home
        self.tmp.cleanup()

    def write_session(self, session_id: str, lines: list[str]) -> None:
        (self.transcripts / f"{session_id}.jsonl").write_text("\n".join(lines) + "\n", encoding="utf-8")

    def ledger_dir(self) -> Path:
        return self.root / ".claude" / "cost-ledger"

    def seed_ledger(self, sessions: dict) -> None:
        self.ledger_dir().mkdir(parents=True, exist_ok=True)
        for session, entry in sessions.items():
            (self.ledger_dir() / f"{session}.json").write_text(json.dumps(entry), encoding="utf-8")

    def stored(self) -> dict:
        return {f.stem: json.loads(f.read_text(encoding="utf-8")) for f in self.ledger_dir().glob("*.json")}

    def test_total_includes_sessions_that_only_the_ledger_knows(self) -> None:
        self.seed_ledger({"other-container": ledger_entry(50.0, 10)})
        self.write_session("local", [message("m1", ACTIVE)])

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 53.0, places=2)
        self.assertEqual(report["sessions"], 2)

    def test_local_transcript_replaces_a_ledger_entry_that_has_fewer_messages(self) -> None:
        self.seed_ledger({"s1": ledger_entry(3.0, 1)})
        self.write_session("s1", [message("m1", IDLE), message("m2", IDLE)])

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 6.0, places=2)
        self.assertEqual(self.stored()["s1"]["messages"], 2)

    def test_ledger_entry_survives_a_local_transcript_that_lost_messages(self) -> None:
        self.seed_ledger({"s1": ledger_entry(9.0, 3)})
        self.write_session("s1", [message("m1", IDLE)])

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 9.0, places=2)
        self.assertEqual(self.stored()["s1"]["messages"], 3)

    def test_a_message_that_two_sessions_both_hold_is_counted_once(self) -> None:
        self.write_session("a-first", [message("shared", IDLE - 100)])
        self.write_session("b-resumed", [message("shared", IDLE), message("own", IDLE)])

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 6.0, places=2)

    def test_idle_session_is_stored_in_full_and_active_one_only_after_a_dollar_of_drift(self) -> None:
        self.write_session("idle", [message("i1", IDLE)])
        self.write_session("active", [message("a1", ACTIVE)])

        cost.build_report(str(self.root), now=NOW)
        self.assertAlmostEqual(self.stored()["idle"]["cost"], 3.0, places=2)
        self.assertAlmostEqual(self.stored()["active"]["cost"], 3.0, places=2)  # new sessions are always stored

        # 10 cents more on the active session stays in memory, 10 cents more on the idle one is flushed.
        self.write_session("idle", [message("i1", IDLE), message("i2", IDLE, 50_000, 0)])
        self.write_session("active", [message("a1", ACTIVE), message("a2", ACTIVE, 50_000, 0)])
        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 6.2, places=2)  # the panel is exact
        self.assertAlmostEqual(self.stored()["idle"]["cost"], 3.1, places=2)
        self.assertAlmostEqual(self.stored()["active"]["cost"], 3.0, places=2)

    def test_only_the_session_that_changed_is_rewritten(self) -> None:
        self.write_session("idle", [message("i1", IDLE)])
        self.write_session("other", [message("o1", IDLE)])
        cost.build_report(str(self.root), now=NOW)
        before = {f.name: f.stat().st_mtime_ns for f in self.ledger_dir().glob("*.json")}

        self.write_session("other", [message("o1", IDLE), message("o2", IDLE)])
        cost.build_report(str(self.root), now=NOW + 5)
        after = {f.name: f.stat().st_mtime_ns for f in self.ledger_dir().glob("*.json")}

        self.assertEqual(after["idle.json"], before["idle.json"])
        self.assertNotEqual(after["other.json"], before["other.json"])

    def test_a_ledger_file_that_is_not_json_is_ignored(self) -> None:
        self.seed_ledger({"good": ledger_entry(4.0, 2)})
        (self.ledger_dir() / "broken.json").write_text("{not json", encoding="utf-8")

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 4.0, places=2)

    def test_report_works_when_the_repository_has_no_claude_folder_yet(self) -> None:
        (self.root / ".claude").rmdir()
        self.write_session("s1", [message("m1", IDLE)])

        report = cost.build_report(str(self.root), now=NOW)

        self.assertAlmostEqual(report["total"], 3.0, places=2)
        self.assertTrue((self.ledger_dir() / "s1.json").exists())


if __name__ == "__main__":
    unittest.main()
