"""跨程序工作鎖，避免多個 MCP client 同時改變同一遊戲狀態。"""

from __future__ import annotations

import json
import os
import platform
import secrets
import subprocess
import time
from collections.abc import Iterator
from contextlib import contextmanager
from pathlib import Path

from rimworld_mcp.core.paths import data_home


def _path(name: str) -> Path:
    return data_home() / "locks" / f"{name}.json"


def _read(path: Path) -> dict[str, object]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {"state": "unknown"}
    return dict(data) if isinstance(data, dict) else {"state": "unknown"}


def _process_alive(pid: int) -> bool:
    """判斷鎖的持有者是否還在；用來接管上一輪異常終止殘留的鎖。"""
    if pid <= 0:
        return False
    if platform.system() == "Windows":
        run = subprocess.run(
            ["tasklist", "/FI", f"PID eq {pid}", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        return f'"{pid}"' in run.stdout
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except OSError:
        # PermissionError 等情況代表程序存在但不屬於我們，仍視為存活。
        return True
    return True


def acquire(name: str, details: dict[str, object] | None = None) -> str:
    """以 O_EXCL 原子建立鎖，並回傳只能由持有者釋放的 token。

    若既有鎖的持有者程序已消失，該鎖視為殘骸並自動接管，避免程序被強制終止後永久卡死。
    """
    path = _path(name)
    path.parent.mkdir(parents=True, exist_ok=True)
    token = secrets.token_urlsafe(18)
    payload = {"token": token, "pid": os.getpid(), "created_at": int(time.time() * 1000), **(details or {})}
    for may_reclaim in (True, False):
        try:
            descriptor = os.open(path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError as exc:
            existing = _read(path)
            holder = existing.get("pid")
            if may_reclaim and isinstance(holder, int) and not _process_alive(holder):
                path.unlink(missing_ok=True)
                continue
            raise RuntimeError(f"已有進行中的 {name} 工作：{existing}") from exc
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=False)
        return token
    raise RuntimeError(f"無法取得 {name} 鎖。")


def release(name: str, token: str | None) -> None:
    path = _path(name)
    if token and _read(path).get("token") == token:
        path.unlink(missing_ok=True)


@contextmanager
def held(name: str, details: dict[str, object] | None = None) -> Iterator[None]:
    token = acquire(name, details)
    try:
        yield
    finally:
        release(name, token)
