from __future__ import annotations

import asyncio
import json
import logging
from pathlib import Path

from rimworld_mcp.core.paths import bridge_port, data_home
from rimworld_mcp.daemon.store import add_diagnostic, read_status

HOST = "127.0.0.1"
ACCEPTED_TYPES = frozenset({"error", "warning", "diagnostic", "loaded_mods", "performance"})


def _expected_token() -> str:
    try:
        return (data_home() / "bridge-token").read_text(encoding="utf-8").strip()
    except OSError:
        return ""


async def handle(reader: asyncio.StreamReader, writer: asyncio.StreamWriter) -> None:
    expected = _expected_token()
    try:
        while line := await reader.readline():
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                # 單一壞行不該拖垮整條連線，後續訊息仍要收。
                continue
            if not isinstance(message, dict):
                continue
            if not expected or message.get("token") != expected:
                continue
            if message.get("type") in ACCEPTED_TYPES:
                add_diagnostic(message)
    except ConnectionError:
        pass
    finally:
        writer.close()
        await writer.wait_closed()


async def run() -> None:
    server = await asyncio.start_server(handle, HOST, bridge_port())
    watcher = asyncio.create_task(watch_player_log())
    try:
        async with server:
            await server.serve_forever()
    finally:
        watcher.cancel()


async def watch_player_log() -> None:
    """Bridge 不可用時，仍從本機 Player.log 收集增量診斷。"""
    offsets: dict[str, int] = {}
    while True:
        status = read_status()
        raw_path = status.get("player_log")
        run_id = status.get("run_id")
        if status.get("state") == "running" and isinstance(raw_path, str) and raw_path:
            path = Path(raw_path)
            if path.is_file():
                offset = offsets.setdefault(str(path), int(status.get("log_offset", 0)))
                size = path.stat().st_size
                if size < offset:
                    offset = 0
                if size > offset:
                    with path.open("r", encoding="utf-8", errors="replace") as stream:
                        stream.seek(offset)
                        for line in stream:
                            text = line.strip()
                            lower = text.lower()
                            if "error" in lower or "exception" in lower or "warning" in lower:
                                kind = "error" if "error" in lower or "exception" in lower else "warning"
                                add_diagnostic(
                                    {
                                        "type": kind,
                                        "source": "player.log",
                                        "run_id": run_id,
                                        "text": text,
                                    }
                                )
                        offsets[str(path)] = stream.tell()
        await asyncio.sleep(0.5)


def main() -> None:
    logging.basicConfig(level=logging.WARNING)
    asyncio.run(run())


if __name__ == "__main__":
    main()
