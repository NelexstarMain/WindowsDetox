#!/usr/bin/env python3
"""Isolated background Haiku runner with Restricted Edit, pre-flight backup, and post-flight rollback."""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import lib_hygiene as lh  # noqa: E402

AUTO_START = "<!-- HYGIENE:AUTO -->"
AUTO_END = "<!-- /HYGIENE:AUTO -->"
LOCK_TTL_SECONDS = 180


def hygiene_settings_path(name: str) -> Path:
    # Prefer the settings vendored next to these hooks (project install), fall back to the global install.
    vendored = Path(__file__).resolve().parent.parent / "hygiene" / name
    return vendored if vendored.exists() else lh.get_claude_home() / "hygiene" / name


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def ensure_claudemd_markers(claude_md: Path) -> None:
    if not claude_md.exists():
        scaffold = (
            "# Project Instructions\n\n"
            "## Manual Rules\n"
            "- Add permanent repository conventions above the auto-hygiene block.\n\n"
            f"{AUTO_START}\n"
            "<!-- Managed automatically by background hygiene. -->\n"
            f"{AUTO_END}\n"
        )
        claude_md.write_text(scaffold, encoding="utf-8")
        return

    content = claude_md.read_text(encoding="utf-8")
    if AUTO_START in content and AUTO_END in content:
        return
    addition = (
        f"\n\n{AUTO_START}\n"
        "<!-- Managed automatically by background hygiene. -->\n"
        f"{AUTO_END}\n"
    )
    claude_md.write_text(content.rstrip() + addition, encoding="utf-8")


def split_marked_sections(text: str) -> tuple[str, str, str] | None:
    start_idx = text.find(AUTO_START)
    end_idx = text.find(AUTO_END)
    if start_idx == -1 or end_idx == -1 or end_idx < start_idx:
        return None
    before = text[: start_idx + len(AUTO_START)]
    body = text[start_idx + len(AUTO_START) : end_idx]
    after = text[end_idx:]
    return before, body, after


def git_status_set(project_root: Path) -> set[str] | None:
    try:
        res = subprocess.run(
            ["git", "-C", str(project_root), "status", "--porcelain", "-uall"],
            capture_output=True,
            text=True,
            timeout=2.0,
            check=False,
        )
        if res.returncode != 0:
            return None
        lines = {line.rstrip() for line in res.stdout.splitlines() if line.strip()}
        return lines
    except Exception:
        return None


def _parse_porcelain_path(line: str) -> str:
    path_part = line[3:] if len(line) > 3 else line
    if " -> " in path_part:
        path_part = path_part.split(" -> ", 1)[1]
    return path_part.strip().strip('"')


def acquire_ttl_lock(lock_path: Path) -> bool:
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    if lock_path.exists():
        try:
            age = time.time() - lock_path.stat().st_mtime
            if age < LOCK_TTL_SECONDS:
                return False
        except Exception:
            return False
    try:
        lock_path.write_text(f"{os.getpid()}\n", encoding="utf-8")
        return True
    except Exception:
        return False


def release_ttl_lock(lock_path: Path) -> None:
    try:
        if lock_path.exists():
            lock_path.unlink()
    except Exception:
        pass


def build_project_prompt(digest_json: str) -> str:
    return (
        "You are an isolated background hygiene assistant.\n"
        "Strict rules:\n"
        "1. Write all documentation in English.\n"
        "2. Edit ONLY the content strictly between <!-- HYGIENE:AUTO --> and <!-- /HYGIENE:AUTO --> in CLAUDE.md.\n"
        "   NEVER alter anything outside those markers.\n"
        "   Keep the auto section concise (<= 45 lines), compressing/merging outdated items.\n"
        "3. Optionally update .claude/hygiene-report.md with suspected dead code, scratch files, or stale references.\n"
        "   NEVER delete files or touch production/test source files.\n\n"
        "Aggregated session observations (JSON):\n"
        f"{digest_json}\n"
    )


def build_global_prompt(project_summaries: str) -> str:
    prefs_path = lh.get_claude_home() / "preferences.md"
    return (
        "You are an isolated global preferences consolidator.\n"
        "Strict rules:\n"
        f"1. Edit ONLY {prefs_path}.\n"
        f"2. Maintain strict line budget: <= {lh.PREFERENCES_MAX_LINES} lines total.\n"
        "   Merge recurring cross-project patterns and replace outdated entries; never append endlessly.\n"
        "3. Write in English, preserving user preferences (Polish chat, English code/docs, strict YAGNI, why-only comments).\n\n"
        "Recent cross-project hygiene digests:\n"
        f"{project_summaries}\n"
    )


def run_haiku_cli(
    prompt: str,
    cwd: Path,
    settings_path: Path,
    allowed_tools: str = "Read,Glob,Grep,Edit",
) -> tuple[int, str]:
    cli_bin = os.environ.get("CLAUDE_HYGIENE_CLI", "claude")
    cmd = [
        cli_bin,
        "-p",
        "--model",
        "haiku",
        "--no-session-persistence",
        "--permission-mode",
        "dontAsk",
        "--tools",
        allowed_tools,
        "--disallowedTools",
        "Bash,Write,NotebookEdit,Agent,WebFetch,WebSearch,AskUserQuestion",
        "--max-turns",
        "4",
        "--max-budget-usd",
        str(lh.HAIKU_BUDGET_USD),
        "--settings",
        str(settings_path),
    ]
    env = os.environ.copy()
    env["HYGIENE_BG"] = "1"
    res = subprocess.run(
        cmd,
        input=prompt,
        cwd=str(cwd),
        env=env,
        capture_output=True,
        text=True,
        timeout=110,
        check=False,
    )
    return res.returncode, (res.stdout + "\n" + res.stderr).strip()


def run_project_hygiene(project_root: Path) -> str:
    proj_dir = lh.project_state_dir(project_root)
    lock_path = proj_dir / "hygiene.lock"
    audit_path = proj_dir / "hygiene-audit.jsonl"
    queue_path = proj_dir / "queue.jsonl"

    if not acquire_ttl_lock(lock_path):
        return "skipped_locked"

    try:
        entries = lh.read_jsonl_safe(queue_path)
        if not entries:
            return "skipped_empty"

        claude_md = project_root / "CLAUDE.md"
        report_md = project_root / ".claude" / "hygiene-report.md"
        report_md.parent.mkdir(parents=True, exist_ok=True)
        if not report_md.exists():
            report_md.write_text("# Hygiene Report\n\n- No issues reported yet.\n", encoding="utf-8")

        ensure_claudemd_markers(claude_md)
        orig_claude = claude_md.read_text(encoding="utf-8")
        orig_parts = split_marked_sections(orig_claude)
        if orig_parts is None:
            return "error_markers"

        claude_bak = proj_dir / "CLAUDE.md.bak"
        report_bak = proj_dir / "hygiene-report.md.bak"
        proj_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(claude_md, claude_bak)
        shutil.copy2(report_md, report_bak)

        pre_git = git_status_set(project_root)
        digest = lh.compact_queue_for_haiku(entries)
        settings_path = hygiene_settings_path("haiku-project-settings.json")

        rc, output_log = run_haiku_cli(
            prompt=build_project_prompt(digest),
            cwd=project_root,
            settings_path=settings_path,
        )

        # Post-flight verification
        violation_reason: str | None = None
        new_claude = claude_md.read_text(encoding="utf-8") if claude_md.exists() else ""
        new_parts = split_marked_sections(new_claude)

        if new_parts is None:
            violation_reason = "hygiene markers removed"
        elif new_parts[0] != orig_parts[0] or new_parts[2] != orig_parts[2]:
            violation_reason = "modified CLAUDE.md outside HYGIENE:AUTO markers"
        elif len(new_parts[1].splitlines()) > 60:
            violation_reason = "HYGIENE:AUTO section exceeded 60-line budget"

        if violation_reason is None and pre_git is not None:
            post_git = git_status_set(project_root)
            if post_git is not None:
                delta = post_git - pre_git
                allowed_paths = {"CLAUDE.md", ".claude/hygiene-report.md"}
                for line in delta:
                    changed_path = _parse_porcelain_path(line)
                    if changed_path not in allowed_paths:
                        violation_reason = f"unauthorized file change detected: {changed_path}"
                        break

        if rc != 0 and violation_reason is None:
            violation_reason = f"cli exited with {rc}"

        if violation_reason is not None:
            shutil.copy2(claude_bak, claude_md)
            shutil.copy2(report_bak, report_md)
            lh.append_jsonl_locked(
                audit_path,
                {
                    "ts": lh.utc_iso_now(),
                    "mode": "project",
                    "status": "rolled_back",
                    "reason": violation_reason,
                    "events": len(entries),
                },
            )
            return f"rolled_back:{violation_reason}"

        # Success: archive processed queue & reset 40m timer
        archive_path = proj_dir / "queue.processed.jsonl"
        with open(archive_path, "a", encoding="utf-8") as afh:
            for item in entries:
                afh.write(lh.json.dumps(item, ensure_ascii=False) + "\n")
        queue_path.write_text("", encoding="utf-8")

        def _reset_timer(meta: dict) -> dict:
            meta["accumulated_s"] = 0
            meta["last_hygiene_at"] = lh.utc_iso_now()
            return meta

        lh.locked_update_json(proj_dir / "project_meta.json", _reset_timer)
        lh.append_jsonl_locked(
            audit_path,
            {
                "ts": lh.utc_iso_now(),
                "mode": "project",
                "status": "applied",
                "events": len(entries),
                "output_tail": output_log[-180:],
            },
        )
        return "applied"
    finally:
        release_ttl_lock(lock_path)


def run_global_hygiene(project_root: Path) -> str:
    claude_home = lh.get_claude_home()
    state_root = lh.get_state_root()
    lock_path = state_root / "global_hygiene.lock"
    audit_path = state_root / "global_hygiene_audit.jsonl"
    prefs_path = claude_home / "preferences.md"

    if not acquire_ttl_lock(lock_path):
        return "skipped_locked"

    try:
        if not prefs_path.exists():
            return "skipped_no_prefs"

        prefs_bak = state_root / "preferences.md.bak"
        prefs_bak.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(prefs_path, prefs_bak)

        # Collect compact snapshots of recent CLAUDE.md files across projects
        summaries: list[str] = []
        projects_dir = state_root / "projects"
        if projects_dir.exists():
            for pdir in sorted(projects_dir.iterdir())[:10]:
                meta = lh.read_json_safe(pdir / "project_meta.json", default={})
                proot = meta.get("project_root")
                if proot:
                    candidate = Path(proot) / "CLAUDE.md"
                    if not candidate.exists():
                        candidate = Path(proot) / ".claude" / "CLAUDE.md"
                    if candidate.exists():
                        txt = candidate.read_text(encoding="utf-8")[:1500]
                        summaries.append(f"## Project: {proot}\n{txt}")

        settings_path = hygiene_settings_path("haiku-global-settings.json")
        rc, output_log = run_haiku_cli(
            prompt=build_global_prompt("\n\n".join(summaries) or "(no project summaries)"),
            cwd=claude_home,
            settings_path=settings_path,
        )

        new_prefs = prefs_path.read_text(encoding="utf-8") if prefs_path.exists() else ""
        lines = new_prefs.splitlines()
        violation_reason: str | None = None
        if not new_prefs.strip():
            violation_reason = "preferences.md emptied"
        elif len(lines) > lh.PREFERENCES_MAX_LINES:
            violation_reason = f"preferences.md exceeded {lh.PREFERENCES_MAX_LINES} lines ({len(lines)})"
        elif rc != 0:
            violation_reason = f"cli exited with {rc}"

        if violation_reason is not None:
            shutil.copy2(prefs_bak, prefs_path)
            lh.append_jsonl_locked(
                audit_path,
                {
                    "ts": lh.utc_iso_now(),
                    "mode": "global",
                    "status": "rolled_back",
                    "reason": violation_reason,
                },
            )
            return f"rolled_back:{violation_reason}"

        def _reset_global(g: dict) -> dict:
            g["accumulated_s"] = 0
            g["last_consolidated_at"] = lh.utc_iso_now()
            return g

        lh.locked_update_json(lh.global_counters_path(), _reset_global)
        lh.append_jsonl_locked(
            audit_path,
            {
                "ts": lh.utc_iso_now(),
                "mode": "global",
                "status": "applied",
                "lines": len(lines),
                "output_tail": output_log[-180:],
            },
        )
        return "applied"
    finally:
        release_ttl_lock(lock_path)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("project", "global"), required=True)
    parser.add_argument("--project-dir", default=None)
    args = parser.parse_args()

    project_root = lh.resolve_project_root(args.project_dir)
    if args.mode == "project":
        status = run_project_hygiene(project_root)
    else:
        status = run_global_hygiene(project_root)
    print(status)


if __name__ == "__main__":
    main()
