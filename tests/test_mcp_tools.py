import asyncio

from rimworld_mcp.mcp_server import mcp


def test_public_tool_names_match_the_contract() -> None:
    expected = {
        "rimworld_status",
        "setup_toolchain",
        "rebuild_index",
        "search_defs",
        "read_symbol",
        "search_source",
        "list_installed_mods",
        "inspect_installed_mod",
        "search_installed_mod_source",
        "configure_workspace",
        "create_mod",
        "build_mod",
        "create_checkpoint",
        "restore_checkpoint",
        "run_test_cycle",
        "test_status",
        "list_test_diagnostics",
        "get_test_diagnostic",
        "stop_test",
        "import_mod_asset",
        "validate_mod_assets",
    }
    # 用公開的 list_tools()，不要碰 SDK 內部的 _tool_manager。
    assert {tool.name for tool in asyncio.run(mcp.list_tools())} == expected
