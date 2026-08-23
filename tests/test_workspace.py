import pytest

from rimworld_mcp.core import workspace


def test_allowed_mod_accepts_paths_inside_a_registered_workspace(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(workspace, "data_home", lambda: tmp_path)
    root = tmp_path / "ws"
    mod = root / "demo.mod"
    mod.mkdir(parents=True)
    workspace.configure_workspace(root)

    assert workspace.allowed_mod(mod) == mod.resolve()


def test_allowed_mod_rejects_paths_outside_every_workspace(tmp_path, monkeypatch) -> None:
    """寫入邊界是本服務唯一的防護；工作區外的路徑必須拒絕。"""
    monkeypatch.setattr(workspace, "data_home", lambda: tmp_path)
    root = tmp_path / "ws"
    root.mkdir()
    outside = tmp_path / "elsewhere"
    outside.mkdir()
    workspace.configure_workspace(root)

    with pytest.raises(PermissionError, match="已登記的工作區"):
        workspace.allowed_mod(outside)


def test_allowed_mod_rejects_traversal_out_of_a_workspace(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(workspace, "data_home", lambda: tmp_path)
    root = tmp_path / "ws"
    (root / "demo.mod").mkdir(parents=True)
    sibling = tmp_path / "secrets"
    sibling.mkdir()
    workspace.configure_workspace(root)

    with pytest.raises(PermissionError):
        workspace.allowed_mod(root / "demo.mod" / ".." / ".." / "secrets")


def test_create_mod_rejects_unregistered_root_and_bad_package_id(tmp_path, monkeypatch) -> None:
    monkeypatch.setattr(workspace, "data_home", lambda: tmp_path)
    root = tmp_path / "ws"
    root.mkdir()

    with pytest.raises(PermissionError):
        workspace.create_mod(root, "Demo", "demo.mod")

    workspace.configure_workspace(root)
    with pytest.raises(ValueError, match="package_id"):
        workspace.create_mod(root, "Demo", "../escape")
