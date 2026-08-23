from __future__ import annotations

import hashlib
import json
import shutil
import time
from pathlib import Path

from rimworld_mcp.core.paths import data_home


def _state_path() -> Path:
    return data_home() / "workspaces.json"


def _canonical(path: str | Path) -> Path:
    resolved = Path(path).expanduser().resolve(strict=True)
    if not resolved.is_dir():
        raise ValueError(f"工作區不是目錄：{resolved}")
    return resolved


def workspaces() -> list[Path]:
    try:
        raw = json.loads(_state_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return []
    return [Path(item) for item in raw.get("roots", []) if Path(item).is_dir()]


def configure_workspace(path: str | Path) -> Path:
    root = _canonical(path)
    roots = {item.resolve() for item in workspaces()}
    roots.add(root)
    _state_path().write_text(json.dumps({"roots": sorted(map(str, roots))}, indent=2), encoding="utf-8")
    return root


def allowed_mod(path: str | Path) -> Path:
    candidate = _canonical(path)
    for root in workspaces():
        try:
            candidate.relative_to(root.resolve())
            return candidate
        except ValueError:
            continue
    raise PermissionError("模組路徑不在已登記的工作區內；請先呼叫 configure_workspace。")


def snapshot_mod(path: str | Path) -> dict[str, str]:
    source = allowed_mod(path)
    snapshots = data_home() / "snapshots" / source.name
    snapshots.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    digest = hashlib.sha256(str(source).encode()).hexdigest()[:10]
    target = snapshots / f"{stamp}-{digest}"
    shutil.copytree(source, target, ignore=shutil.ignore_patterns("bin", "obj", ".git"))
    return {"id": target.name, "path": str(target)}


def restore_mod(path: str | Path, snapshot_id: str, confirm: bool) -> dict[str, str]:
    if not confirm:
        raise PermissionError("還原快照需要 confirm=true。")
    target = allowed_mod(path)
    source = data_home() / "snapshots" / target.name / snapshot_id
    if not source.is_dir():
        raise FileNotFoundError(f"找不到快照：{snapshot_id}")
    before = snapshot_mod(target)
    for child in target.iterdir():
        if child.name == ".git":
            continue
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()
    for child in source.iterdir():
        destination = target / child.name
        if child.is_dir():
            shutil.copytree(child, destination)
        else:
            shutil.copy2(child, destination)
    return {"restored": snapshot_id, "pre_restore_snapshot": before["id"]}


def create_mod(root: str | Path, name: str, package_id: str, with_code: bool = False) -> Path:
    workspace = _canonical(root)
    if workspace.resolve() not in {item.resolve() for item in workspaces()}:
        raise PermissionError("請先以 configure_workspace 登記目標根目錄。")
    if not package_id or any(ch not in "abcdefghijklmnopqrstuvwxyz0123456789._-" for ch in package_id.lower()):
        raise ValueError("package_id 只能包含英數字、句點、底線與連字號。")
    mod = workspace / package_id
    mod.mkdir(parents=False, exist_ok=False)
    for directory in ("About", "Defs", "Patches", "Textures", "Sounds", "Source"):
        (mod / directory).mkdir()
    (mod / "About/About.xml").write_text(
        f"<ModMetaData>\n  <name>{name}</name>\n  <packageId>{package_id}</packageId>\n  <supportedVersions><li>1.6</li></supportedVersions>\n</ModMetaData>\n",
        encoding="utf-8",
    )
    if with_code:
        (mod / "Source" / f"{package_id}.csproj").write_text(
            """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <RimWorldManagedDir Condition=\"'$(RimWorldManagedDir)' == ''\">$(RIMWORLD_MANAGED_DIR)</RimWorldManagedDir>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=\"Assembly-CSharp\"><HintPath>$(RimWorldManagedDir)/Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include=\"UnityEngine.CoreModule\"><HintPath>$(RimWorldManagedDir)/UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
    <PackageReference Include=\"Microsoft.NETFramework.ReferenceAssemblies.net472\" Version=\"1.0.3\" PrivateAssets=\"all\" />
  </ItemGroup>
</Project>
""",
            encoding="utf-8",
        )
    return mod
