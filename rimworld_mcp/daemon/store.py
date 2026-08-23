from __future__ import annotations

import hashlib
import json
import time
from pathlib import Path
from typing import Any

from rimworld_mcp.core.paths import data_home


def diagnostics_path() -> Path:
    return data_home() / "diagnostics.json"


def status_path() -> Path:
    return data_home() / "test-status.json"


def daemon_path() -> Path:
    return data_home() / "daemon.json"


def read_daemon() -> dict[str, Any]:
    """讀取 daemon 自述檔；用來分辨連接埠是本服務的 daemon 還是別的程序占著。"""
    try:
        record = json.loads(daemon_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return dict(record) if isinstance(record, dict) else {}


def write_daemon(record: dict[str, Any]) -> None:
    daemon_path().write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")


def clear_daemon(pid: int) -> None:
    """只清掉自己寫的那一筆，避免把後繼 daemon 的紀錄誤刪。"""
    if read_daemon().get("pid") == pid:
        daemon_path().unlink(missing_ok=True)


def read_diagnostics() -> list[dict[str, Any]]:
    try:
        return list(json.loads(diagnostics_path().read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        return []


def add_diagnostic(event: dict[str, Any]) -> dict[str, Any]:
    events = read_diagnostics()
    text = str(event.get("text", ""))
    stack = str(event.get("stack", ""))
    signature = stack or "\n".join(line for line in text.splitlines() if " at " in line) or text
    digest_input = f"{event.get('type', 'diagnostic')}:{signature}".encode()
    event["hash"] = event.get("hash") or hashlib.sha256(digest_input).hexdigest()[:16]
    event["at"] = event.get("at") or int(time.time() * 1000)
    for old in events:
        if old.get("hash") == event["hash"]:
            old["count"] = int(old.get("count", 1)) + 1
            old.update(event)
            break
    else:
        event["count"] = 1
        events.append(event)
    diagnostics_path().write_text(json.dumps(events[-200:], ensure_ascii=False, indent=2), encoding="utf-8")
    return event


def write_status(status: dict[str, Any]) -> None:
    status_path().write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding="utf-8")


def read_status() -> dict[str, Any]:
    try:
        return dict(json.loads(status_path().read_text(encoding="utf-8")))
    except (OSError, json.JSONDecodeError):
        return {"state": "idle"}
