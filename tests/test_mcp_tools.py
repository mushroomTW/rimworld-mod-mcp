import asyncio

from rimworld_mcp.mcp_server import mcp


def test_public_tool_names_match_the_contract() -> None:
    expected = {
        "rimworld_status",
        "setup_toolchain",
        "rebuild_index",
        "search_defs",
        "read_def",
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


def test_every_tool_declares_annotations() -> None:
    """註記是客戶端判斷唯讀／破壞性的唯一依據，不得漏掉。"""
    tools = asyncio.run(mcp.list_tools())
    assert all(tool.annotations and tool.annotations.title for tool in tools)
    destructive = {
        tool.name
        for tool in tools
        if tool.annotations and tool.annotations.destructive_hint
    }
    assert destructive == {"restore_checkpoint", "stop_test"}


def test_search_defs_omits_xml_by_default() -> None:
    """xml 欄位可達數 MB，預設不得出現在搜尋結果的 schema 中。"""
    tool = next(t for t in asyncio.run(mcp.list_tools()) if t.name == "search_defs")
    assert "include_xml" in tool.input_schema["properties"]
    assert tool.input_schema["properties"]["include_xml"]["default"] is False
