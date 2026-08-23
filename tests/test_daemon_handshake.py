"""daemon 佔埠判定與測試工作階段偵測的回歸測試。"""

import socket

import pytest

from rimworld_mcp.core import test_cycle


@pytest.fixture
def occupied_port(monkeypatch: pytest.MonkeyPatch):
    """佔住一個真實的 loopback 埠，模擬「埠已被占用」。"""
    listener = socket.socket()
    listener.bind((test_cycle.DAEMON_HOST, 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    monkeypatch.setenv("RIMWORLD_MCP_BRIDGE_PORT", str(port))
    try:
        yield port
    finally:
        listener.close()


def test_foreign_process_on_port_is_reported_not_silently_reused(
    occupied_port: int, monkeypatch: pytest.MonkeyPatch
) -> None:
    """埠被別人占著卻當成自家 daemon，會讓整輪測試靜默失去 Bridge 診斷。"""
    monkeypatch.setattr(test_cycle, "read_daemon", dict)

    result = test_cycle._ensure_daemon()

    assert result["state"] == "unavailable"
    assert result["pid"] is None
    assert str(occupied_port) in str(result["reason"])


def test_own_daemon_on_port_is_reused(occupied_port: int, monkeypatch: pytest.MonkeyPatch) -> None:
    import os

    monkeypatch.setattr(
        test_cycle, "read_daemon", lambda: {"pid": os.getpid(), "port": occupied_port}
    )

    result = test_cycle._ensure_daemon()

    assert result["state"] == "reused"
    assert result["pid"] is None


def test_stale_daemon_record_on_occupied_port_is_not_reused(
    occupied_port: int, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setattr(
        test_cycle, "read_daemon", lambda: {"pid": 999_999_999, "port": occupied_port}
    )
    monkeypatch.setattr(test_cycle, "process_alive", lambda _: False)

    assert test_cycle._ensure_daemon()["state"] == "unavailable"


def test_active_session_requires_a_live_game_process(monkeypatch: pytest.MonkeyPatch) -> None:
    """狀態檔留著 running 但遊戲早就結束時，不能擋住下一輪測試。"""
    monkeypatch.setattr(
        test_cycle, "read_status", lambda: {"state": "running", "pid": 4242, "run_id": "r1"}
    )
    monkeypatch.setattr(test_cycle, "process_alive", lambda _: False)

    assert test_cycle._active_test_session() is None

    monkeypatch.setattr(test_cycle, "process_alive", lambda _: True)
    session = test_cycle._active_test_session()

    assert session is not None and session["run_id"] == "r1"
