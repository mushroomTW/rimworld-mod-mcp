from __future__ import annotations

import argparse
import json
from typing import Any

from mcp.server import MCPServer

from rimworld_mcp.core.assets import import_asset, validate_assets
from rimworld_mcp.core.build import build_mod
from rimworld_mcp.core.index import RimWorldIndex
from rimworld_mcp.core.mods import installed_mods
from rimworld_mcp.core.paths import detect_rimworld
from rimworld_mcp.core.test_cycle import run_test_cycle, stop_test
from rimworld_mcp.core.workspace import configure_workspace, create_mod, restore_mod, snapshot_mod
from rimworld_mcp.daemon.store import read_diagnostics, read_status

mcp = MCPServer("rimworld-mcp", instructions="本機 RimWorld 索引、建置與隔離測試工具。所有寫入只允許在已登記工作區內。")
index = RimWorldIndex()


@mcp.tool()
def rimworld_status() -> dict[str, object]:
    """回傳偵測到的 RimWorld 路徑、索引狀態與目前測試狀態。"""
    return {"rimworld": detect_rimworld().as_json(), "index": index.status(), "test": read_status()}


@mcp.tool()
def setup_toolchain(confirm: bool = False) -> dict[str, str]:
    """還原專案私有的 ILSpyCmd；會下載 .NET tool，必須 confirm=true。"""
    return index.setup_toolchain(confirm)


@mcp.tool()
def rebuild_index() -> dict[str, object]:
    """本機反編譯 RimWorld 與 DLC，重建 Def、符號及來源索引。"""
    return index.rebuild()


@mcp.tool()
def search_defs(query: str, def_type: str | None = None, limit: int = 25) -> list[dict[str, object]]:
    """以 Def 名稱、標籤或描述搜尋已索引的 Core 與 DLC XML Def。"""
    return index.search_defs(query, def_type, limit)


@mcp.tool()
def read_symbol(name: str, max_bytes: int = 4096) -> list[dict[str, object]]:
    """讀取已反編譯 RimWorld C# 的類別、方法或其他符號。"""
    return index.read_symbol(name, max_bytes)


@mcp.tool()
def search_source(pattern: str, file_pattern: str = "*", limit: int = 200) -> list[dict[str, object]]:
    """以 Python regex 搜尋反編譯 C# 與已複製的 Def XML。"""
    return index.search_source(pattern, file_pattern, limit)


@mcp.tool()
def list_installed_mods() -> list[dict[str, object]]:
    """列出本機 Mods 與 Steam Workshop 中含有效 About.xml 的模組。"""
    return [{"package_id": item.package_id, "name": item.name, "path": str(item.path), "dependencies": item.dependencies, "source": item.source} for item in installed_mods(detect_rimworld())]


@mcp.tool()
def inspect_installed_mod(package_id: str) -> dict[str, object]:
    """回傳已安裝 Mod 的 metadata，並按需反編譯其 Assemblies（結果以檔案指紋快取於本機）。"""
    match = next((item for item in installed_mods(detect_rimworld()) if item.package_id == package_id.lower()), None)
    if not match:
        raise ValueError(f"找不到已安裝 Mod：{package_id}")
    return {
        "package_id": match.package_id,
        "name": match.name,
        "path": str(match.path),
        "dependencies": match.dependencies,
        "assemblies": index.inspect_mod(match.path),
    }


@mcp.tool()
def search_installed_mod_source(
    package_id: str, pattern: str, limit: int = 100
) -> list[dict[str, object]]:
    """按需反編譯指定已安裝 Mod，並搜尋其 C# 原始碼快取。"""
    match = next(
        (item for item in installed_mods(detect_rimworld()) if item.package_id == package_id.lower()),
        None,
    )
    if not match:
        raise ValueError(f"找不到已安裝 Mod：{package_id}")
    return index.search_mod_source(match.path, pattern, limit)


@mcp.tool(name="configure_workspace")
def configure_workspace_tool(path: str) -> dict[str, str]:
    """登記可由 MCP 修改與測試的 Mod 工作區根目錄。"""
    return {"workspace": str(configure_workspace(path))}


@mcp.tool(name="create_mod")
def create_mod_tool(workspace: str, name: str, package_id: str, with_code: bool = False) -> dict[str, str]:
    """在已登記工作區中建立 RimWorld XML-only 或含 C# 骨架的 Mod。"""
    return {"mod": str(create_mod(workspace, name, package_id, with_code))}


@mcp.tool(name="build_mod")
def build_mod_tool(path: str) -> dict[str, object]:
    """驗證 XML-only Mod 或以 dotnet build 建置 C# Mod。"""
    return build_mod(path)


@mcp.tool()
def create_checkpoint(path: str) -> dict[str, str]:
    """對已登記工作區內的 Mod 建立可還原快照。"""
    return snapshot_mod(path)


@mcp.tool()
def restore_checkpoint(path: str, snapshot_id: str, confirm: bool = False) -> dict[str, str]:
    """還原 Mod 快照；必須 confirm=true。"""
    return restore_mod(path, snapshot_id, confirm)


@mcp.tool(name="run_test_cycle")
def run_test_cycle_tool(path: str, companion_mods: list[str] | None = None, quicktest: bool = True) -> dict[str, object]:
    """以隔離 savedata、最小依賴集、Bridge 及 Player.log 監控啟動 RimWorld。"""
    return run_test_cycle(path, companion_mods, quicktest)


@mcp.tool()
def test_status() -> dict[str, object]:
    """回傳目前或最近一次 RimWorld MCP 測試工作階段。"""
    return read_status()


@mcp.tool()
def list_test_diagnostics() -> list[dict[str, Any]]:
    """列出目前／最近測試的去重錯誤與警告摘要。"""
    return read_diagnostics()


@mcp.tool()
def get_test_diagnostic(hash: str) -> dict[str, Any]:
    """依 hash 取得完整測試診斷。"""
    item = next((event for event in read_diagnostics() if event.get("hash") == hash), None)
    if not item:
        raise ValueError(f"找不到診斷：{hash}")
    return item


@mcp.tool(name="stop_test")
def stop_test_tool(confirm: bool = False, terminate_game: bool = False) -> dict[str, Any]:
    """清理本服務建立的測試 Mod／Bridge 連結與監控 daemon；必須 confirm=true。

    terminate_game=true 時另外終止本服務啟動的遊戲程序；使用者自行啟動的 RimWorld 不受影響。
    """
    return stop_test(confirm, terminate_game)


@mcp.tool()
def import_mod_asset(path: str, source_path: str, kind: str) -> dict[str, str]:
    """將 PNG/JPG 圖片或 OGG/WAV 音效安全匯入已登記 Mod 的 Textures 或 Sounds。"""
    return import_asset(path, source_path, kind)


@mcp.tool()
def validate_mod_assets(path: str) -> list[dict[str, object]]:
    """檢查已登記 Mod 的資產副檔名、檔案標頭與過大檔案。"""
    return validate_assets(path)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("command", nargs="?", default="stdio", choices=["stdio", "setup_toolchain"])
    parser.add_argument("--confirm", action="store_true")
    args = parser.parse_args()
    if args.command == "setup_toolchain":
        print(json.dumps(index.setup_toolchain(args.confirm), ensure_ascii=False))
        return
    mcp.run(transport="stdio")


if __name__ == "__main__":
    main()
