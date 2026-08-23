"""測試工作階段偵測的回歸測試。"""

import pytest

from rimworld_mcp.core import test_cycle


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
