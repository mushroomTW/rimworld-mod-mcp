from __future__ import annotations

import os
import platform
import secrets
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as etree
from contextlib import suppress
from pathlib import Path
from typing import Any

from rimworld_mcp.core.bridge import ensure_bridge
from rimworld_mcp.core.locks import acquire, process_alive, release
from rimworld_mcp.core.mods import builtin_packs, installed_mods, parse_about, resolve_test_order
from rimworld_mcp.core.paths import bridge_port, data_home, detect_rimworld
from rimworld_mcp.core.workspace import allowed_mod
from rimworld_mcp.daemon.store import read_status, write_status

DAEMON_HOST = "127.0.0.1"
LINK_PREFIX = "RimWorldMcp-"


def _mods_config(version: str, active: list[str], known_expansions: list[str]) -> str:
    root = etree.Element("ModsConfigData")
    etree.SubElement(root, "version").text = version
    active_node = etree.SubElement(root, "activeMods")
    for package_id in active:
        etree.SubElement(active_node, "li").text = package_id
    expansions = etree.SubElement(root, "knownExpansions")
    for package_id in known_expansions:
        etree.SubElement(expansions, "li").text = package_id
    return etree.tostring(root, encoding="unicode")


def _config_values(real_config: Path | None) -> tuple[str, list[str]]:
    if not real_config:
        return "1.6", []
    try:
        root = etree.parse(real_config).getroot()
        return (root.findtext("version") or "1.6").strip(), [item.text.strip().lower() for item in root.findall("./knownExpansions/li") if item.text]
    except etree.ParseError:
        return "1.6", []


def _owned_link(link: Path, target: Path) -> bool:
    """只認本服務自己建立、且確實指向預期目標的連結。

    Windows 的 directory junction 在 CPython 中 is_symlink() 回傳 False，所以必須同時接受目錄。
    """
    if not link.name.startswith(LINK_PREFIX):
        return False
    try:
        return (link.is_symlink() or link.is_dir()) and link.resolve() == target.resolve()
    except OSError:
        return False


def _link(link: Path, target: Path) -> None:
    """建立 junction（Windows）或 symbolic link；已是本服務指向同一目標的連結視為成功。"""
    if _owned_link(link, target):
        return
    if link.exists() or link.is_symlink():
        raise FileExistsError(f"拒絕覆寫既有 Mod 連結：{link}")
    if platform.system() == "Windows":
        run = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], capture_output=True, text=True, check=False)
        if run.returncode:
            raise RuntimeError(run.stderr or run.stdout)
    else:
        link.symlink_to(target, target_is_directory=True)


def _remove_link(link: Path, target: Path) -> None:
    """移除本服務建立的連結本身，絕不動它指向的 Mod 內容。"""
    try:
        if not _owned_link(link, target):
            return
        if link.is_symlink():
            link.unlink()
        elif platform.system() == "Windows" and link.is_dir():
            # cmd/rmdir 只移除 junction，不會刪除它指向的 Mod。
            subprocess.run(["cmd", "/c", "rmdir", str(link)], capture_output=True, check=False)
    except OSError:
        pass


def _terminate(pid: int) -> bool:
    """終止本服務自己啟動的程序。pid 已回收或無權限時安靜失敗。"""
    if pid <= 0:
        return False
    try:
        os.kill(pid, signal.SIGTERM)
    except (OSError, ValueError):
        return False
    return True


def _reap(pid: int) -> None:
    """回收本服務啟動、已結束的子程序。

    遊戲與 daemon 都以 Popen 啟動且從不 wait，POSIX 上結束後會留下 zombie；
    zombie 在 os.kill(pid, 0) 與 ps 都仍表現為存活，會讓後續測試永遠開不起來。
    """
    # 用 sys.platform 而非 platform.system()，讓 mypy 能收斂平台、認得 os.WNOHANG。
    if pid <= 0 or sys.platform == "win32":
        return
    with suppress(ChildProcessError, OSError):
        os.waitpid(pid, os.WNOHANG)


def _active_test_session() -> dict[str, Any] | None:
    """回傳仍在執行中的本服務測試工作階段；順帶回收已結束的子程序。"""
    status = read_status()
    for key in ("pid", "daemon_pid"):
        value = status.get(key)
        if isinstance(value, int):
            _reap(value)
    pid = status.get("pid")
    if status.get("state") == "running" and isinstance(pid, int) and process_alive(pid):
        return status
    return None


def _ensure_daemon() -> int | None:
    """確保監控 daemon 在跑，回傳本次啟動的 pid；沿用既有 daemon 時回傳 None。

    埠已被佔用代表已有 daemon，Bridge 仍可連線；daemon 完全起不來時 Player.log 仍是備援來源。
    """
    import socket

    with socket.socket() as sock:
        if sock.connect_ex((DAEMON_HOST, bridge_port())) == 0:
            return None
    process = subprocess.Popen(
        [sys.executable, "-m", "rimworld_mcp.daemon.runner"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        start_new_session=True,
    )
    return process.pid


def _game_is_running(executable: Path) -> bool:
    """只檢查，不干預使用者既有的 RimWorld 程序。"""
    if platform.system() == "Windows":
        run = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {executable.name}", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        return executable.name.lower() in run.stdout.lower()
    run = subprocess.run(
        ["ps", "-A", "-o", "comm="],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    return any(Path(line.strip()).name == executable.name for line in run.stdout.splitlines())


def _prepare_bridge(bridge_source: Path, bridge_link: Path, managed_dir: Path | None) -> dict[str, Any]:
    """建置並連結診斷 Bridge。

    Bridge 不可用不該讓整個測試循環失敗（Player.log 仍能提供診斷），但也不靜默降級：
    回傳值會寫進 test status，使用者可用 test_status 看到確切原因。
    """
    info = parse_about(bridge_source, "bridge")
    if not info:
        return {"state": "unavailable", "reason": f"找不到有效的 {bridge_source / 'About/About.xml'}。"}
    try:
        ensure_bridge(bridge_source, managed_dir)
        _link(bridge_link, bridge_source)
    except (OSError, RuntimeError) as exc:
        return {"state": "unavailable", "reason": str(exc)}
    return {"state": "active", "package_id": info.package_id}


def run_test_cycle(path: str, companions: list[str] | None = None, quicktest: bool = True) -> dict[str, Any]:
    mod = allowed_mod(path)
    paths = detect_rimworld()
    if not paths.executable or not paths.mods_dir:
        raise FileNotFoundError("找不到可啟動的 RimWorld 或其 Mods 目錄。")
    session = _active_test_session()
    if session:
        raise RuntimeError(
            f"本服務啟動的測試工作階段（run_id={session.get('run_id')}）仍在執行；"
            "請先呼叫 stop_test(confirm=true) 結束後再開始新的一輪。"
        )
    if _game_is_running(paths.executable):
        raise RuntimeError("偵測到使用者已啟動 RimWorld；為避免干擾，請先自行關閉後再開始隔離測試。")
    target = parse_about(mod, "workspace")
    if not target:
        raise ValueError("目標 Mod 缺少有效 About/About.xml 與 packageId。")
    # Core 與 DLC 位於 Data/，不在 installed_mods() 的掃描範圍內，必須另外併入。
    available = [*builtin_packs(paths), *installed_mods(paths)]
    order = resolve_test_order(target, available, companions or [])
    if order.missing:
        raise ValueError("缺少相依 Mod：" + ", ".join(order.missing))

    run_id = f"{int(time.time())}-{secrets.token_hex(4)}"
    bridge_source = Path(__file__).resolve().parents[2] / "bridge"
    test_link = paths.mods_dir / f"{LINK_PREFIX}Test-{target.package_id}"
    bridge_link = paths.mods_dir / f"{LINK_PREFIX}Bridge"
    lock_token = acquire("test", {"operation": "run_test_cycle", "run_id": run_id})
    daemon_pid: int | None = None
    try:
        savedata = data_home() / "test-savedata" / run_id
        config_dir = savedata / "Config"
        config_dir.mkdir(parents=True)

        # 先建立連結、再寫 ModsConfig.xml，確保宣告啟用的與實際連結的一致。
        _link(test_link, mod)
        bridge = _prepare_bridge(bridge_source, bridge_link, paths.managed_dir)
        active = [*order.active]
        bridge_package_id = bridge.get("package_id")
        if isinstance(bridge_package_id, str):
            active.append(bridge_package_id)

        version, expansions = _config_values(paths.mods_config)
        (config_dir / "ModsConfig.xml").write_text(_mods_config(version, active, expansions), encoding="utf-8")
        if paths.prefs_xml:
            (config_dir / "Prefs.xml").write_bytes(paths.prefs_xml.read_bytes())

        token = secrets.token_urlsafe(32)
        (data_home() / "bridge-token").write_text(token, encoding="utf-8")
        port = bridge_port()
        player_log = paths.player_log
        status: dict[str, Any] = {
            "state": "starting",
            "run_id": run_id,
            "started_at": int(time.time() * 1000),
            "savedata": str(savedata),
            "mod": str(mod),
            "active_mods": active,
            "skipped_load_after": order.skipped_load_after,
            "bridge": bridge,
            "bridge_port": port,
            "player_log": str(player_log) if player_log else None,
            "log_offset": player_log.stat().st_size if player_log and player_log.is_file() else 0,
            "links": [
                {"link": str(test_link), "target": str(mod)},
                {"link": str(bridge_link), "target": str(bridge_source)},
            ],
        }
        write_status(status)
        # daemon_pid 必須在啟動遊戲之前就落地，否則遊戲啟動失敗時 stop_test 找不到 daemon。
        daemon_pid = _ensure_daemon()
        status["daemon_pid"] = daemon_pid
        write_status(status)
        args = [f"-savedatafolder={savedata}"] + (["-quicktest"] if quicktest else [])
        environment = {**os.environ, "SteamAppId": "294100", "SteamGameId": "294100", "RIMWORLD_MCP_BRIDGE_TOKEN": token, "RIMWORLD_MCP_BRIDGE_PORT": str(port)}
        process = subprocess.Popen([str(paths.executable), *args], cwd=paths.install_root, env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
        status.update({"state": "running", "pid": process.pid})
        write_status(status)
        return status
    except BaseException:
        _remove_link(test_link, mod)
        _remove_link(bridge_link, bridge_source)
        if daemon_pid is not None:
            # 只回收這一輪自己起的 daemon；沿用既有 daemon 時 daemon_pid 是 None。
            _terminate(daemon_pid)
        raise
    finally:
        # 鎖只保護「建立連結 → 寫設定 → 啟動遊戲」這段臨界區。啟動之後改由
        # _active_test_session() 維持一次只跑一場；否則忘記 stop_test 就會把鎖
        # 留在長壽的 MCP server 程序名下，殘骸回收永遠不會觸發而卡死後續測試。
        release("test", lock_token)


def stop_test(confirm: bool, terminate_game: bool = False) -> dict[str, Any]:
    """清理本服務建立的測試連結與監控 daemon。

    terminate_game=True 時另外終止「本服務自己啟動的」遊戲程序；
    使用者自行啟動的 RimWorld 從不在此範圍內，因為它的 pid 不會被記錄。
    """
    if not confirm:
        raise PermissionError("停止／清理測試需要 confirm=true。")
    status = read_status()
    for item in status.get("links", []):
        if isinstance(item, dict):
            _remove_link(Path(str(item.get("link", ""))), Path(str(item.get("target", ""))))
    terminated: dict[str, bool] = {}
    daemon_pid = status.get("daemon_pid")
    if isinstance(daemon_pid, int):
        terminated["daemon"] = _terminate(daemon_pid)
    game_pid = status.get("pid")
    if terminate_game and isinstance(game_pid, int):
        terminated["game"] = _terminate(game_pid)
    for pid in (daemon_pid, game_pid):
        if isinstance(pid, int):
            _reap(pid)
    write_status({"state": "stopped", "previous_run": status.get("run_id"), "terminated": terminated})
    return read_status()
