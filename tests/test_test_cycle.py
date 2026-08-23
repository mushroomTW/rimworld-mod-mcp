import xml.etree.ElementTree as etree
from pathlib import Path

from rimworld_mcp.core.mods import CORE_PACKAGE_ID, ModInfo, resolve_test_order
from rimworld_mcp.core.test_cycle import _mods_config, _owned_link

BRIDGE_PACKAGE_ID = "rimworldmcp.bridge"


def _active_mods(xml: str) -> list[str]:
    root = etree.fromstring(xml)
    return [item.text or "" for item in root.findall("./activeMods/li")]


def _mod(package_id: str, **kwargs: object) -> ModInfo:
    return ModInfo(package_id, package_id, Path("/mods") / package_id, (), "local", **kwargs)  # type: ignore[arg-type]


def test_mods_config_lists_core_first_and_bridge_last() -> None:
    """Core 不啟用遊戲根本跑不起來；Bridge 不啟用則所有 Bridge 診斷都收不到。"""
    order = resolve_test_order(_mod("target"), [], [])
    xml = _mods_config("1.6", [*order.active, BRIDGE_PACKAGE_ID], ["ludeon.rimworld.royalty"])

    active = _active_mods(xml)

    assert active[0] == CORE_PACKAGE_ID
    assert active[-1] == BRIDGE_PACKAGE_ID
    assert "target" in active


def test_core_is_activated_from_data_dir_pack_when_available() -> None:
    core = _mod(CORE_PACKAGE_ID)
    order = resolve_test_order(_mod("target"), [core], [])

    assert order.active == [CORE_PACKAGE_ID, "target"]
    assert order.missing == []


def test_dlc_dependency_resolves_from_builtin_packs() -> None:
    royalty = _mod("ludeon.rimworld.royalty")
    target = ModInfo("target", "Target", Path("/mods/target"), ("ludeon.rimworld.royalty",), "workspace")

    order = resolve_test_order(target, [royalty], [])

    assert order.missing == []
    assert order.active.index("ludeon.rimworld.royalty") < order.active.index("target")


def test_missing_load_after_is_skipped_not_reported_missing() -> None:
    """loadAfter 是軟性排序提示。把它當硬相依會讓大量正常 mod 無法測試。"""
    target = ModInfo(
        "target",
        "Target",
        Path("/mods/target"),
        (),
        "workspace",
        load_after=("some.optional.mod", "ludeon.rimworld.biotech"),
    )

    order = resolve_test_order(target, [], [])

    assert order.missing == []
    assert order.skipped_load_after == ["ludeon.rimworld.biotech", "some.optional.mod"]
    assert "target" in order.active


def test_missing_hard_dependency_is_still_reported() -> None:
    target = ModInfo("target", "Target", Path("/mods/target"), ("required.mod",), "workspace")

    order = resolve_test_order(target, [], [])

    assert order.missing == ["required.mod"]


def test_load_after_cycle_does_not_raise() -> None:
    first = _mod("first", load_after=("second",))
    second = _mod("second", load_after=("first",))

    order = resolve_test_order(first, [first, second], [])

    assert set(order.active) == {CORE_PACKAGE_ID, "first", "second"}


def test_owned_link_only_accepts_service_prefixed_names(tmp_path: Path) -> None:
    """清理只能碰本服務建立的連結，絕不能誤刪使用者自己的 Mod 目錄。"""
    target = tmp_path / "DemoMod"
    target.mkdir()
    foreign = tmp_path / "SomeUserMod"
    foreign.mkdir()

    assert not _owned_link(foreign, foreign)
    assert not _owned_link(tmp_path / "RimWorldMcp-Test-demo", target)
