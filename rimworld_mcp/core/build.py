from __future__ import annotations

import os
import shutil
import subprocess
import xml.etree.ElementTree as etree

from rimworld_mcp.core.paths import detect_rimworld
from rimworld_mcp.core.workspace import allowed_mod


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
    if run.returncode == 0:
        output = projects[0].parent / "bin" / "Release" / "net472"
        assemblies = mod / "Assemblies"
        assemblies.mkdir(exist_ok=True)
        for library in output.glob("*.dll"):
            destination = assemblies / library.name
            shutil.copy2(library, destination)
            deployed.append(str(destination.relative_to(mod)))
    return {
        "success": run.returncode == 0,
        "kind": "csharp",
        "project": str(projects[0]),
        "deployed": deployed,
        "stdout": run.stdout[-12000:],
        "stderr": run.stderr[-12000:],
    }
