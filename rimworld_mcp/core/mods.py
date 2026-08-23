from __future__ import annotations

import xml.etree.ElementTree as etree
from dataclasses import dataclass
from pathlib import Path
from typing import NamedTuple

from rimworld_mcp.core.paths import RimWorldPaths

CORE_PACKAGE_ID = "ludeon.rimworld"


@dataclass(frozen=True, slots=True)
class ModInfo:
    package_id: str
    name: str
    path: Path
    dependencies: tuple[str, ...]
    source: str
    load_after: tuple[str, ...] = ()
    load_before: tuple[str, ...] = ()
    incompatible_with: tuple[str, ...] = ()


class TestOrder(NamedTuple):
    """隔離測試的解析結果。

    active：寫進 ModsConfig.xml 的啟用順序，Core 必為第一項。
    missing：找不到的硬相依（modDependencies）與使用者指定的 companion。
    skipped_load_after：找不到的 loadAfter 宣告；loadAfter 是軟性排序提示，缺少不算錯誤。
    """

    active: list[str]
    missing: list[str]
    skipped_load_after: list[str]


def _package_ids(root: etree.Element, xpath: str) -> tuple[str, ...]:
    values: list[str] = []
    for node in root.findall(xpath):
        value = (node.findtext("packageId") or node.text or "").strip().lower()
        if value:
            values.append(value)
    return tuple(values)


def parse_about(mod_dir: Path, source: str) -> ModInfo | None:
    about = mod_dir / "About/About.xml"
    try:
        root = etree.parse(about).getroot()
    except (OSError, etree.ParseError):
        return None
    package_id = (root.findtext("packageId") or "").strip().lower()
    if not package_id:
        return None
    dependencies = _package_ids(root, "./modDependencies/li")
    return ModInfo(
        package_id,
        (root.findtext("name") or mod_dir.name).strip(),
        mod_dir,
        dependencies,
        source,
        _package_ids(root, "./loadAfter/li"),
        _package_ids(root, "./loadBefore/li"),
        _package_ids(root, "./incompatibleWith/li"),
    )


def builtin_packs(paths: RimWorldPaths) -> list[ModInfo]:
    """收集 Core 與已安裝的官方 DLC。

    這些 pack 位於 Data/ 而非 Mods/ 或 Workshop，所以 installed_mods() 看不到它們；
    但 ModsConfig.xml 的 activeMods 必須包含 Core，DLC 也只有列進去才會載入。
    """
    if not paths.data_dir:
        return []
    packs: list[ModInfo] = []
    for child in sorted(paths.data_dir.iterdir()):
        if not child.is_dir():
            continue
        info = parse_about(child, "core" if child.name.lower() == "core" else "expansion")
        if info:
            packs.append(info)
    return packs


def installed_mods(paths: RimWorldPaths) -> list[ModInfo]:
    sources = [(paths.mods_dir, "local"), (paths.workshop_dir, "workshop")]
    mods: list[ModInfo] = []
    for root, source in sources:
        if not root:
            continue
        for child in root.iterdir():
            if child.is_dir():
                info = parse_about(child, source)
                if info:
                    mods.append(info)
    return sorted(mods, key=lambda item: (item.name.lower(), item.package_id))


def resolve_test_order(
    target: ModInfo, available: list[ModInfo], companions: list[str]
) -> TestOrder:
    by_id = {item.package_id: item for item in available}
    by_id[target.package_id] = target
    ordered: list[str] = []
    missing: list[str] = []
    skipped: list[str] = []
    active: set[str] = set()
    complete: set[str] = set()

    def visit(package_id: str, *, required: bool) -> None:
        if package_id in complete:
            return
        if package_id in active:
            # 硬相依循環無法自動化解；loadAfter 循環只是排序提示衝突，忽略即可。
            if required:
                raise ValueError(f"偵測到 Mod 相依循環：{package_id}")
            return
        mod = by_id.get(package_id)
        if not mod:
            (missing if required else skipped).append(package_id)
            return
        active.add(package_id)
        for dependency in mod.dependencies:
            visit(dependency, required=True)
        for dependency in mod.load_after:
            visit(dependency, required=False)
        active.remove(package_id)
        complete.add(package_id)
        ordered.append(package_id)

    # Core 必須啟用且排在最前。它位於 Data/ 之下，偵測不到時仍要列出，
    # 因為 RimWorld 是從 Data/ 解析 Core，不需要它出現在 available 內。
    if CORE_PACKAGE_ID in by_id:
        visit(CORE_PACKAGE_ID, required=True)
    else:
        complete.add(CORE_PACKAGE_ID)
        ordered.append(CORE_PACKAGE_ID)

    visit(target.package_id, required=True)
    for companion in companions:
        visit(companion.lower(), required=True)
    selected = set(ordered)
    conflicts = sorted(
        f"{package_id} ↔ {conflict}"
        for package_id in selected
        for conflict in (by_id[package_id].incompatible_with if package_id in by_id else ())
        if conflict in selected
    )
    if conflicts:
        raise ValueError("偵測到不相容 Mod：" + ", ".join(conflicts))
    return TestOrder(ordered, sorted(set(missing)), sorted(set(skipped)))
