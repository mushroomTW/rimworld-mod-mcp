"""量測本專案賴以運作的跨平台行為，而不是靠假設。

這些契約若在某個 OS 或 Python 版本上改變，#4（junction 判斷）、#7（stale lock 接管）、
#5／#11（回收自己啟動的程序）就會靜默失效，所以在 CI 的三個 OS 上都要實測。
"""

import os
import platform
import subprocess
import sys
import time
from pathlib import Path

import pytest

from rimworld_mcp.core.locks import _process_alive
from rimworld_mcp.core.test_cycle import _link, _owned_link, _remove_link, _terminate

WINDOWS = platform.system() == "Windows"


def _demo_mod(root: Path) -> Path:
    mod = root / "DemoMod"
    (mod / "About").mkdir(parents=True)
    (mod / "About" / "About.xml").write_text("<ModMetaData/>", encoding="utf-8")
    return mod


def test_link_is_idempotent_for_its_own_link(tmp_path: Path) -> None:
    """#4：Windows junction 不是 symlink，靠 is_symlink() 判斷會讓第二次測試直接失敗。"""
    target = _demo_mod(tmp_path)
    link = tmp_path / "RimWorldMcp-Test-demo.mod"

    _link(link, target)
    assert _owned_link(link, target)
    assert (link / "About" / "About.xml").read_text(encoding="utf-8") == "<ModMetaData/>"

    _link(link, target)  # 不得拋出

    assert _owned_link(link, target)


@pytest.mark.skipif(not WINDOWS, reason="junction 是 Windows 專屬的 reparse point")
def test_windows_junction_is_not_reported_as_a_symlink(tmp_path: Path) -> None:
    """這就是 _owned_link() 不能只看 is_symlink() 的原因，變了要立刻知道。"""
    target = _demo_mod(tmp_path)
    link = tmp_path / "RimWorldMcp-Test-demo.mod"
    _link(link, target)

    assert link.is_symlink() is False
    assert link.is_dir() is True
    assert link.resolve() == target.resolve()


def test_link_refuses_to_overwrite_a_link_pointing_elsewhere(tmp_path: Path) -> None:
    target = _demo_mod(tmp_path)
    other = tmp_path / "OtherTarget"
    other.mkdir()
    link = tmp_path / "RimWorldMcp-Test-demo.mod"
    _link(link, target)

    with pytest.raises((FileExistsError, RuntimeError)):
        _link(link, other)


def test_remove_link_deletes_the_link_and_keeps_the_mod(tmp_path: Path) -> None:
    target = _demo_mod(tmp_path)
    link = tmp_path / "RimWorldMcp-Test-demo.mod"
    _link(link, target)

    _remove_link(link, target)

    assert not link.exists()
    assert (target / "About" / "About.xml").is_file()


def test_remove_link_ignores_directories_it_did_not_create(tmp_path: Path) -> None:
    """清理絕不能因為路徑湊巧吻合就刪掉使用者自己的目錄。"""
    foreign = tmp_path / "SomeUserMod"
    (foreign / "About").mkdir(parents=True)

    _remove_link(foreign, foreign)

    assert foreign.is_dir()


def test_process_alive_distinguishes_live_from_exited(tmp_path: Path) -> None:
    """#7：方向錯了會比原本的死鎖更糟——恆 False 等於併發保護消失。"""
    assert _process_alive(os.getpid()) is True

    exited = subprocess.Popen([sys.executable, "-c", "pass"])
    exited.wait()
    time.sleep(0.4)

    assert _process_alive(exited.pid) is False
    assert _process_alive(0) is False
    assert _process_alive(-1) is False


def test_terminate_actually_stops_a_process_we_started() -> None:
    """#5／#11：回收路徑在修正後從未被執行過，這裡實跑一次。"""
    victim = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(60)"])
    try:
        assert _process_alive(victim.pid) is True
        assert _terminate(victim.pid) is True
        assert victim.wait(timeout=15) is not None
    finally:
        if victim.poll() is None:
            victim.kill()
            victim.wait(timeout=10)

    assert _terminate(-1) is False
