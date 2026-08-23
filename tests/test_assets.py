from rimworld_mcp.core import assets


def test_import_and_validate_png_asset(tmp_path, monkeypatch) -> None:
    mod = tmp_path / "DemoMod"
    mod.mkdir()
    source = tmp_path / "icon.png"
    source.write_bytes(b"\x89PNG\r\n\x1a\nminimal")
    monkeypatch.setattr(assets, "allowed_mod", lambda _: mod)

    imported = assets.import_asset(str(mod), str(source), "texture")

    assert imported["reference"] == "Textures/icon"
    assert assets.validate_assets(str(mod)) == []


def test_validate_assets_reports_bad_audio_header(tmp_path, monkeypatch) -> None:
    mod = tmp_path / "DemoMod"
    sound = mod / "Sounds" / "broken.ogg"
    sound.parent.mkdir(parents=True)
    sound.write_bytes(b"not-an-ogg")
    monkeypatch.setattr(assets, "allowed_mod", lambda _: mod)

    diagnostics = assets.validate_assets(str(mod))

    assert diagnostics[0]["level"] == "error"
