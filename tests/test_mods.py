from pathlib import Path

from rimworld_mcp.core.mods import CORE_PACKAGE_ID, ModInfo, resolve_test_order


def test_dependency_order_places_dependency_first() -> None:
    root = Path("/mods")
    base = ModInfo("base", "Base", root / "base", (), "local")
    target = ModInfo("target", "Target", root / "target", ("base",), "workspace")
    order = resolve_test_order(target, [base], [])
    # Core 一律強制排在最前，即使它不在 available 內（RimWorld 從 Data/ 解析）。
    assert order.active == [CORE_PACKAGE_ID, "base", "target"]
    assert order.missing == []
    assert order.skipped_load_after == []


def test_rejects_incompatible_companion() -> None:
    root = Path("/mods")
    target = ModInfo("target", "Target", root / "target", (), "workspace", incompatible_with=("other",))
    other = ModInfo("other", "Other", root / "other", (), "local")

    try:
        resolve_test_order(target, [other], ["other"])
    except ValueError as exc:
        assert "不相容" in str(exc)
    else:
        raise AssertionError("預期偵測到不相容 Mod")
