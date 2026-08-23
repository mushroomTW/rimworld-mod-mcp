from __future__ import annotations

import os
import platform
import re
from collections.abc import Iterable
from dataclasses import asdict, dataclass
from pathlib import Path

from platformdirs import user_cache_path, user_data_path

APP_NAME = "RimWorldMcp"
STEAM_APP_ID = "294100"
DEFAULT_BRIDGE_PORT = 49460


@dataclass(frozen=True, slots=True)
class RimWorldPaths:
    install_root: Path | None
    executable: Path | None
    managed_dir: Path | None
    data_dir: Path | None
    mods_dir: Path | None
    workshop_dir: Path | None
    player_log: Path | None
    mods_config: Path | None
    prefs_xml: Path | None

    def as_json(self) -> dict[str, str | None]:
        return {key: str(value) if value else None for key, value in asdict(self).items()}


def data_home() -> Path:
    return Path(user_data_path(APP_NAME, ensure_exists=True))


def cache_home() -> Path:
    return Path(user_cache_path(APP_NAME, ensure_exists=True))


def config_path() -> Path:
    return data_home() / "config.json"


def bridge_port() -> int:
    """Bridge 的 loopback 埠。daemon、埠探測與傳給遊戲的環境變數都必須用這一個來源。"""
    raw = os.environ.get("RIMWORLD_MCP_BRIDGE_PORT", "").strip()
    if raw.isdigit() and 0 < int(raw) < 65536:
        return int(raw)
    return DEFAULT_BRIDGE_PORT


def configured_install_root() -> Path | None:
    raw = os.environ.get("RIMWORLD_MCP_GAME_PATH")
    if raw:
        candidate = Path(raw).expanduser().resolve()
        if candidate.exists():
            return candidate
    return None


def _steam_roots() -> list[Path]:
    home = Path.home()
    system = platform.system()
    if system == "Windows":
        roots = [Path(os.environ.get("PROGRAMFILES(X86)", "C:/Program Files (x86)")) / "Steam"]
        roots.append(Path(os.environ.get("PROGRAMFILES", "C:/Program Files")) / "Steam")
    elif system == "Darwin":
        roots = [home / "Library/Application Support/Steam"]
    else:
        roots = [home / ".steam/steam", home / ".local/share/Steam"]
    return [root for root in roots if root.exists()]


def _library_roots() -> Iterable[Path]:
    for steam in _steam_roots():
        yield steam
        library_file = steam / "steamapps/libraryfolders.vdf"
        try:
            text = library_file.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        for raw in re.findall(r'"path"\s+"([^"]+)"', text):
            candidate = Path(raw.replace("\\\\", "\\"))
            if candidate.exists():
                yield candidate


def _install_candidates() -> list[Path]:
    override = configured_install_root()
    candidates = [override] if override else []
    candidates.extend(root / "steamapps/common/RimWorld" for root in _library_roots())
    return [candidate for candidate in candidates if candidate is not None]


def _mac_bundle(root: Path) -> Path:
    return root / "RimWorldMac.app"


def _mac_executable(bundle: Path) -> Path | None:
    plist = bundle / "Contents/Info.plist"
    names: list[str] = []
    try:
        text = plist.read_text(encoding="utf-8", errors="replace")
        match = re.search(r"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>", text)
        if match:
            names.append(match.group(1).strip())
    except OSError:
        pass
    names.extend(["RimWorld by Ludeon Studios", "RimWorldMac"])
    for name in names:
        candidate = bundle / "Contents/MacOS" / name
        if candidate.is_file():
            return candidate
    files = list((bundle / "Contents/MacOS").glob("*")) if (bundle / "Contents/MacOS").exists() else []
    return files[0] if len(files) == 1 and files[0].is_file() else None


def _user_config_candidates(system: str) -> tuple[list[Path], list[Path]]:
    home = Path.home()
    if system == "Windows":
        base = Path(os.environ.get("USERPROFILE", str(home))) / "AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
        return [base / "Config/ModsConfig.xml"], [base / "Player.log"]
    if system == "Darwin":
        support = home / "Library/Application Support"
        return [support / "RimWorld/Config/ModsConfig.xml", support / "RimWorld by Ludeon Studios/Config/ModsConfig.xml"], [home / "Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"]
    base = home / ".config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios"
    return [base / "Config/ModsConfig.xml"], [base / "Player.log", home / ".config/unity3d/Ludeon Studios/RimWorld/Player.log"]


def detect_rimworld() -> RimWorldPaths:
    system = platform.system()
    install = next((path for path in _install_candidates() if path.exists()), None)
    if install is None:
        return RimWorldPaths(None, None, None, None, None, None, None, None, None)

    executable: Path | None
    if system == "Windows":
        managed = install / "RimWorldWin64_Data/Managed"
        executable = install / "RimWorldWin64.exe"
        data = install / "Data"
    elif system == "Darwin":
        bundle = _mac_bundle(install)
        managed = bundle / "Contents/Resources/Data/Managed"
        executable = _mac_executable(bundle)
        data = install / "Data"
    else:
        managed = install / "RimWorldLinux_Data/Managed"
        executable = install / "RimWorldLinux"
        data = install / "Data"

    config_candidates, log_candidates = _user_config_candidates(system)
    log_override = os.environ.get("RIMWORLD_MCP_PLAYER_LOG")
    player_log_candidates = ([Path(log_override).expanduser()] if log_override else []) + log_candidates
    mods_config = next((path for path in config_candidates if path.is_file()), None)
    return RimWorldPaths(
        install_root=install,
        executable=executable if executable and executable.is_file() else None,
        managed_dir=managed if managed.is_dir() else None,
        data_dir=data if (data / "Core/Defs").is_dir() else None,
        mods_dir=(install / "Mods") if (install / "Mods").is_dir() else None,
        workshop_dir=(install.parent.parent / f"workshop/content/{STEAM_APP_ID}") if (install.parent.parent / f"workshop/content/{STEAM_APP_ID}").is_dir() else None,
        player_log=next((path for path in player_log_candidates if path.is_file()), None),
        mods_config=mods_config,
        prefs_xml=(mods_config.parent / "Prefs.xml") if mods_config and (mods_config.parent / "Prefs.xml").is_file() else None,
    )
