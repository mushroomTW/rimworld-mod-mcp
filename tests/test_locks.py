import pytest

from rimworld_mcp.core import locks


def test_lock_rejects_second_holder_and_releases(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(locks, "data_home", lambda: tmp_path)

    token = locks.acquire("test", {"run_id": "one"})
    with pytest.raises(RuntimeError, match="已有進行中的"):
        locks.acquire("test")
    locks.release("test", token)

    assert locks.acquire("test")


def test_lock_held_by_dead_process_is_reclaimed(tmp_path, monkeypatch) -> None:
    """程序被強制終止後留下的鎖必須能自動接管，否則該工作永遠無法再執行。"""
    monkeypatch.setattr(locks, "data_home", lambda: tmp_path)
    locks.acquire("index", {"operation": "rebuild_index"})
    monkeypatch.setattr(locks, "_process_alive", lambda _: False)

    assert locks.acquire("index")


def test_lock_held_by_live_process_is_not_reclaimed(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(locks, "data_home", lambda: tmp_path)
    locks.acquire("index")
    monkeypatch.setattr(locks, "_process_alive", lambda _: True)

    with pytest.raises(RuntimeError, match="已有進行中的"):
        locks.acquire("index")
