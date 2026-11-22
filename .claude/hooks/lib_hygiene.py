#!/usr/bin/env python3
"""Shared stdlib utilities for Claude Code Hygiene System hooks."""

from __future__ import annotations

import contextlib
import fcntl
import fnmatch
import hashlib
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Callable

# Configured thresholds & policy constants
WARN_CONTEXT_PCT = 30.0
ALERT_CONTEXT_PCT = 50.0
LONG_TURN_SECONDS = 180
PROJECT_HYGIENE_MINUTES = 40
GLOBAL_CONSOLIDATION_HOURS = 10
STOP_RETRY_CAP = 1
HAIKU_BUDGET_USD = 0.20
PREFERENCES_MAX_LINES = 70
HANDOFF_MAX_AGE_SECONDS = 24 * 3600
BG_RETRY_COOLDOWN_SECONDS = 600

IGNORED_PATH_PATTERNS = (
    "*.md",
    ".claude/**",
    "docs/**",
    "*.txt",
    ".gitignore",
    ".gitattributes",
    ".editorconfig",
    "LICENSE",
    "*.png",
    "*.jpg",
    "*.jpeg",
    "*.svg",
    "*.ico",
)

TEST_COMMAND_PREFIXES = (
    ("dotnet", "test"),
    ("dotnet", "build"),
    ("npm", "test"),
    ("npm", "run", "test"),
    ("pnpm", "test"),
    ("pnpm", "run", "test"),
    ("yarn", "test"),
    ("yarn", "run", "test"),
    ("bun", "test"),
    ("bun", "run", "test"),
    ("npx", "vitest"),
    ("npx", "jest"),
    ("npx", "tsx", "--test"),
    ("node", "--test"),
    ("tsx", "--test"),
    ("pytest",),
    ("py.test",),
    ("python", "-m", "pytest"),
    ("python3", "-m", "pytest"),
    ("uv", "run", "pytest"),
    ("poetry", "run", "pytest"),
    ("python", "-m", "unittest"),
    ("python3", "-m", "unittest"),
    ("make", "test"),
    ("make", "check"),
    ("cargo", "test"),
    ("go", "test"),
    ("ctest",),
)


def is_bg_hygiene() -> bool:
    return os.environ.get("HYGIENE_BG") == "1"


def get_claude_home() -> Path:
    override = os.environ.get("CLAUDE_HYGIENE_HOME")
    if override:
        return Path(override).expanduser().resolve()
    return (Path.home() / ".claude").resolve()


def get_state_root() -> Path:
    return get_claude_home() / "state"


def read_stdin_json() -> dict[str, Any]:
    try:
        raw = sys.stdin.read()
        if not raw or not raw.strip():
            return {}
        data = json.loads(raw)
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def resolve_project_root(cwd: str | None = None) -> Path:
    base = Path(cwd).expanduser() if cwd else Path.cwd()
    if not base.exists():
        base = Path.cwd()
    base = base.resolve()
    try:
        res = subprocess.run(
            ["git", "-C", str(base), "rev-parse", "--show-toplevel"],
            capture_output=True,
            text=True,
            timeout=1.5,
            check=False,
        )
        if res.returncode == 0 and res.stdout.strip():
            return Path(res.stdout.strip()).resolve()
    except Exception:
        pass
    return base


def project_id_for(project_root: Path) -> str:
    return hashlib.sha1(str(project_root).encode("utf-8")).hexdigest()[:12]


def session_state_path(session_id: str) -> Path:
    safe_id = re.sub(r"[^A-Za-z0-9_.-]", "_", session_id or "default")
    return get_state_root() / "sessions" / f"{safe_id}.json"


def project_state_dir(project_root: Path) -> Path:
    return get_state_root() / "projects" / project_id_for(project_root)


def global_counters_path() -> Path:
    return get_state_root() / "global_counters.json"


@contextlib.contextmanager
def file_lock(lock_path: Path):
    lock_path.parent.mkdir(parents=True, exist_ok=True)
    with open(lock_path, "a+", encoding="utf-8") as fh:
        fcntl.flock(fh.fileno(), fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(fh.fileno(), fcntl.LOCK_UN)


def read_json_safe(path: Path, default: Any = None) -> Any:
    try:
        if not path.exists():
            return {} if default is None else default
        with open(path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
            return data
    except Exception:
        return {} if default is None else default


def write_json_atomic(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = path.with_name(f".{path.name}.tmp.{os.getpid()}")
    with open(tmp_path, "w", encoding="utf-8") as fh:
        json.dump(data, fh, ensure_ascii=False, indent=2)
        fh.flush()
        os.fsync(fh.fileno())
    os.replace(tmp_path, path)


def locked_update_json(
    path: Path,
    updater: Callable[[dict[str, Any]], dict[str, Any]],
) -> dict[str, Any]:
    lock_path = path.with_name(f"{path.name}.lock")
    with file_lock(lock_path):
        current = read_json_safe(path, default={})
        if not isinstance(current, dict):
            current = {}
        updated = updater(current)
        write_json_atomic(path, updated)
        return updated


def append_jsonl_locked(path: Path, entry: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lock_path = path.with_name(f"{path.name}.lock")
    line = json.dumps(entry, ensure_ascii=False) + "\n"
    with file_lock(lock_path):
        with open(path, "a", encoding="utf-8") as fh:
            fh.write(line)
            fh.flush()


def read_jsonl_safe(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    items: list[dict[str, Any]] = []
    try:
        with open(path, "r", encoding="utf-8") as fh:
            for line in fh:
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                    if isinstance(obj, dict):
                        items.append(obj)
                except Exception:
                    continue
    except Exception:
        return []
    return items


def load_project_hygiene_config(project_root: Path) -> dict[str, Any]:
    cfg_path = project_root / ".claude" / "hygiene.json"
    data = read_json_safe(cfg_path, default={})
    return data if isinstance(data, dict) else {}


def is_ignored_path(file_path: str, project_root: Path) -> bool:
    if not file_path:
        return True
    p = Path(file_path)
    try:
        rel = p.resolve().relative_to(project_root.resolve()).as_posix()
    except Exception:
        rel = p.as_posix().lstrip("./")

    rel_lower = rel.lower()
    name_lower = Path(rel).name.lower()

    cfg = load_project_hygiene_config(project_root)
    extra_ignored = cfg.get("extra_ignored_paths")
    patterns = list(IGNORED_PATH_PATTERNS)
    if isinstance(extra_ignored, list):
        patterns.extend(str(x) for x in extra_ignored if isinstance(x, str) and x.strip())

    for pattern in patterns:
        pat = pattern.lower()
        if pat.endswith("/**"):
            prefix = pat[:-3].rstrip("/")
            if rel_lower == prefix or rel_lower.startswith(prefix + "/"):
                return True
        elif "/" not in pat:
            if fnmatch.fnmatch(name_lower, pat) or fnmatch.fnmatch(rel_lower, pat):
                return True
        else:
            if fnmatch.fnmatch(rel_lower, pat):
                return True
    return False


def _strip_env_prefix(tokens: list[str]) -> list[str]:
    idx = 0
    while idx < len(tokens):
        tok = tokens[idx]
        if tok in ("env", "sudo", "nice", "nohup", "time"):
            idx += 1
            continue
        if re.match(r"^[A-Za-z_][A-Za-z0-9_]*=.*", tok):
            idx += 1
            continue
        break
    return tokens[idx:]


def is_test_or_build_command(command: str, project_root: Path | None = None) -> bool:
    if not command or not command.strip():
        return False

    extra_cmds: list[tuple[str, ...]] = []
    if project_root is not None:
        cfg = load_project_hygiene_config(project_root)
        raw_extra = cfg.get("extra_test_commands")
        if isinstance(raw_extra, list):
            for entry in raw_extra:
                if isinstance(entry, str) and entry.strip():
                    parts = [t.lower() for t in entry.strip().split()]
                    parts[0] = parts[0].removeprefix("./")
                    extra_cmds.append(tuple(parts))

    segments = re.split(r"&&|\|\||;|\n", command)
    for seg in segments:
        seg = seg.strip()
        if not seg:
            continue
        sub_segs = [s.strip() for s in seg.split("|") if s.strip()]
        for sub in sub_segs:
            raw_tokens = [t.strip("\"'") for t in sub.split()]
            tokens = _strip_env_prefix(raw_tokens)
            if not tokens:
                continue
            cmd_name = Path(tokens[0]).name.lower()
            tail = [t.lower() for t in tokens[1:]]
            normalized = [cmd_name] + tail
            path_normalized = [tokens[0].lower().removeprefix("./")] + tail

            for prefix in (*TEST_COMMAND_PREFIXES, *extra_cmds):
                plen = len(prefix)
                if (len(normalized) >= plen and tuple(normalized[:plen]) == prefix) or (
                    len(path_normalized) >= plen and tuple(path_normalized[:plen]) == prefix
                ):
                    return True

            # Docker compose / docker run wrapping a test runner
            if cmd_name in ("docker", "docker-compose") and any(
                t in ("pytest", "test", "dotnet") for t in normalized[1:]
            ) and ("exec" in normalized or "run" in normalized):
                return True

            if "godot" in cmd_name and any("test" in t or "--headless" == t for t in normalized[1:]):
                return True
            if "unity" in cmd_name and "-runtests" in normalized[1:]:
                return True
    return False


def extract_context_pct(
    session_state: dict[str, Any],
    transcript_path: str | None,
) -> float | None:
    cached = session_state.get("context_used_pct")
    if isinstance(cached, (int, float)):
        return float(cached)
    if not transcript_path:
        return None
    tp = Path(transcript_path)
    if not tp.exists():
        return None
    try:
        size = tp.stat().st_size
        read_offset = max(0, size - 65536)
        with open(tp, "rb") as fh:
            fh.seek(read_offset)
            chunk = fh.read().decode("utf-8", errors="ignore")
        for line in reversed(chunk.splitlines()):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                continue
            msg = obj.get("message") if isinstance(obj, dict) else None
            usage = msg.get("usage") if isinstance(msg, dict) else None
            if isinstance(usage, dict):
                inp = int(usage.get("input_tokens", 0) or 0)
                cache_read = int(usage.get("cache_read_input_tokens", 0) or 0)
                cache_create = int(usage.get("cache_creation_input_tokens", 0) or 0)
                total = inp + cache_read + cache_create
                if total > 0:
                    return round((total / 200000.0) * 100.0, 1)
    except Exception:
        return None
    return None


def compact_queue_for_haiku(entries: list[dict[str, Any]], max_chars: int = 7500) -> str:
    """Pre-aggregate queue entries to minimize Haiku input tokens."""
    if not entries:
        return "[]"
    error_counts: dict[str, int] = {}
    all_files: list[str] = []
    seen_files: set[str] = set()
    condensed: list[dict[str, Any]] = []

    for item in entries[-30:]:
        for f in item.get("files", []) or []:
            if f not in seen_files:
                seen_files.add(f)
                all_files.append(f)
        for err in item.get("errors", []) or []:
            key = str(err)[:160]
            error_counts[key] = error_counts.get(key, 0) + 1
        condensed.append(
            {
                "ts": item.get("ts"),
                "trigger": item.get("trigger"),
                "dur_s": item.get("duration_s"),
                "intent": str(item.get("intent", ""))[:140],
                "tests": item.get("tests"),
                "outcome": str(item.get("outcome_tail", ""))[:180],
            }
        )

    payload = {
        "events_count": len(entries),
        "unique_files_touched": all_files[:40],
        "aggregated_errors": [
            {"count": cnt, "error": msg}
            for msg, cnt in sorted(error_counts.items(), key=lambda x: -x[1])[:12]
        ],
        "recent_events": condensed[-15:],
    }
    text = json.dumps(payload, ensure_ascii=False, indent=2)
    if len(text) > max_chars:
        text = text[: max_chars - 20] + "\n... [truncated]"
    return text


def utc_iso_now() -> str:
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
