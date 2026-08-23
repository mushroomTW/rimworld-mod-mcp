"""安全匯入並檢查 RimWorld Mod 的圖片與音效資產。"""

from __future__ import annotations

import shutil
from pathlib import Path
from typing import Literal

from rimworld_mcp.core.workspace import allowed_mod

IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg"}
AUDIO_EXTENSIONS = {".ogg", ".wav"}

# 以 Literal 表達，讓允許值直接出現在 MCP 工具的 input schema，而不是只在執行期才驗。
AssetKind = Literal["texture", "sound"]


def import_asset(mod_path: str, source_path: str, kind: AssetKind | str) -> dict[str, str]:
    mod = allowed_mod(mod_path)
    source = Path(source_path).expanduser().resolve(strict=True)
    if not source.is_file():
        raise ValueError("資產來源必須是檔案。")
    normalized_kind = kind.lower()
    expected = IMAGE_EXTENSIONS if normalized_kind == "texture" else AUDIO_EXTENSIONS if normalized_kind == "sound" else set()
    if source.suffix.lower() not in expected:
        raise ValueError(f"{normalized_kind or kind} 不接受 {source.suffix} 格式。")
    destination_root = mod / ("Textures" if normalized_kind == "texture" else "Sounds")
    destination_root.mkdir(exist_ok=True)
    destination = destination_root / source.name
    if destination.exists() and destination.resolve() != source:
        raise FileExistsError(f"目標資產已存在：{destination.name}")
    shutil.copy2(source, destination)
    return {
        "asset": str(destination),
        "reference": str(destination.relative_to(mod).with_suffix("")).replace("\\", "/"),
    }


def validate_assets(mod_path: str) -> list[dict[str, object]]:
    mod = allowed_mod(mod_path)
    diagnostics: list[dict[str, object]] = []
    for folder, extensions in (("Textures", IMAGE_EXTENSIONS), ("Sounds", AUDIO_EXTENSIONS)):
        root = mod / folder
        if not root.is_dir():
            continue
        for asset in root.rglob("*"):
            if not asset.is_file():
                continue
            suffix = asset.suffix.lower()
            relative = str(asset.relative_to(mod)).replace("\\", "/")
            if suffix not in extensions:
                diagnostics.append({"level": "warning", "file": relative, "message": "不支援的資產副檔名。"})
                continue
            with asset.open("rb") as stream:
                header = stream.read(12)
            valid = (
                (suffix == ".png" and header.startswith(b"\x89PNG\r\n\x1a\n"))
                or (suffix in {".jpg", ".jpeg"} and header.startswith(b"\xff\xd8"))
                or (suffix == ".wav" and header.startswith(b"RIFF") and header[8:12] == b"WAVE")
                or (suffix == ".ogg" and header.startswith(b"OggS"))
            )
            if not valid:
                diagnostics.append({"level": "error", "file": relative, "message": "檔案內容與副檔名不符或已損毀。"})
            if asset.stat().st_size > 50 * 1024 * 1024:
                diagnostics.append({"level": "warning", "file": relative, "message": "資產超過 50 MiB，可能拖慢載入。"})
    return diagnostics
