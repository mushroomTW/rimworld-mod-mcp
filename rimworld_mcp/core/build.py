from __future__ import annotations

import os
import shutil
import subprocess
import xml.etree.ElementTree as etree
from contextlib import suppress
from pathlib import Path

from rimworld_mcp.core.paths import detect_rimworld
from rimworld_mcp.core.workspace import allowed_mod


def _target_frameworks(project: Path) -> list[str]:
    """讀出 csproj 宣告的 TargetFramework(s)；相容有無 MSBuild 命名空間兩種寫法。"""
    frameworks: list[str] = []
    with suppress(OSError, etree.ParseError):
        for node in etree.parse(project).getroot().iter():
            if node.tag.rsplit("}", 1)[-1] in {"TargetFramework", "TargetFrameworks"}:
                frameworks.extend(part.strip() for part in (node.text or "").split(";") if part.strip())
    return frameworks


def _output_dirs(project: Path) -> list[Path]:
    """列出可能的 Release 輸出目錄，不再假設一定是 net472。

    優先用 csproj 宣告的 TFM，其次掃描 bin/Release 下的子目錄，最後接受沒有 TFM
    子目錄的輸出配置；否則使用者換一個 TargetFramework 就會「建置成功但沒部署」。
    """
    release = project.parent / "bin" / "Release"
    candidates = [release / name for name in _target_frameworks(project)]
    if release.is_dir():
        candidates.extend(sorted(item for item in release.iterdir() if item.is_dir()))
        candidates.append(release)
    ordered: list[Path] = []
    seen: set[str] = set()
    for item in candidates:
        key = str(item)
        if key not in seen and item.is_dir():
            seen.add(key)
            ordered.append(item)
    return ordered


def build_mod(path: str) -> dict[str, object]:
    mod = allowed_mod(path)
    about = mod / "About/About.xml"
    try:
        root = etree.parse(about).getroot()
    except (OSError, etree.ParseError) as exc:
        raise ValueError(f"About.xml 無效：{exc}") from exc
    if root.tag != "ModMetaData" or not (root.findtext("packageId") or "").strip():
        raise ValueError("About.xml 必須包含 ModMetaData 與 packageId。")
    projects = list((mod / "Source").rglob("*.csproj"))
    if not projects:
        return {"success": True, "kind": "xml", "message": "XML-only Mod 結構有效，無需 C# 建置。"}
    if len(projects) != 1:
        raise ValueError("Source 下必須恰有一個 .csproj，才可安全建置。")
    paths = detect_rimworld()
    environment = None
    if paths.managed_dir:
        environment = {**os.environ, "RIMWORLD_MANAGED_DIR": str(paths.managed_dir)}
    run = subprocess.run(
        ["dotnet", "build", str(projects[0]), "-c", "Release", "--nologo"],
        cwd=mod,
        capture_output=True,
        env=environment,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    deployed: list[str] = []
    warnings: list[str] = []
    output_dir: Path | None = None
    if run.returncode == 0:
        assemblies = mod / "Assemblies"
        for candidate in _output_dirs(projects[0]):
            libraries = sorted(candidate.glob("*.dll"))
            if not libraries:
                continue
            output_dir = candidate
            assemblies.mkdir(exist_ok=True)
            for library in libraries:
                destination = assemblies / library.name
                shutil.copy2(library, destination)
                deployed.append(str(destination.relative_to(mod)))
            break
        if not deployed:
            warnings.append(
                f"建置回報成功，但在 {projects[0].parent / 'bin' / 'Release'} 下找不到任何輸出 DLL；"
                "請確認 csproj 的 TargetFramework 與 OutputPath 設定。"
            )
    return {
        "success": run.returncode == 0,
        "kind": "csharp",
        "project": str(projects[0]),
        "output_dir": str(output_dir) if output_dir else None,
        "deployed": deployed,
        "warnings": warnings,
        "stdout": run.stdout[-12000:],
        "stderr": run.stderr[-12000:],
    }
