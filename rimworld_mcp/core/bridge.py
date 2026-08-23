"""建置並暫存本機診斷 Bridge，不打包任何遊戲組件。"""

from __future__ import annotations

import os
import shutil
import subprocess
from pathlib import Path


def ensure_bridge(bridge_root: Path, managed_dir: Path | None) -> Path:
    """僅在暫存 DLL 過期時建置 Bridge，並回傳 Assemblies 目錄。"""
    bridge_root = bridge_root.resolve()
    if not managed_dir or not (managed_dir / "Assembly-CSharp.dll").is_file():
        raise FileNotFoundError("Bridge 建置需要 RimWorld Managed/Assembly-CSharp.dll。")
    project = bridge_root / "Source" / "RimWorldMcp.Bridge.csproj"
    assemblies = bridge_root / "Assemblies"
    output = bridge_root / "Source" / "bin" / "Release" / "net472" / "RimWorldMcp.Bridge.dll"
    staged = assemblies / output.name
    sources = [project, *(bridge_root / "Source").glob("*.cs")]
    stale = not staged.is_file() or any(path.stat().st_mtime_ns > staged.stat().st_mtime_ns for path in sources)
    if stale:
        environment = {**os.environ, "RIMWORLD_MANAGED_DIR": str(managed_dir)}
        run = subprocess.run(
            ["dotnet", "build", str(project), "-c", "Release", "--nologo"],
            cwd=bridge_root,
            env=environment,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        if run.returncode or not output.is_file():
            detail = (run.stderr or run.stdout or "")[-3000:]
            raise RuntimeError(f"Bridge 建置失敗：{detail}")
        assemblies.mkdir(parents=True, exist_ok=True)
        shutil.copy2(output, staged)
        harmony = output.parent / "0Harmony.dll"
        if harmony.is_file():
            shutil.copy2(harmony, assemblies / harmony.name)
    return assemblies
