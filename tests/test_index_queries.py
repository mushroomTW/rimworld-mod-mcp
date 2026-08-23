"""索引查詢層的回歸測試：payload 大小、FTS5 語法、LIKE 萬用字元。"""

from pathlib import Path

import pytest

from rimworld_mcp.core import index as index_module
from rimworld_mcp.core.index import RimWorldIndex, _fts_match, _like_literal

B = chr(92)

BIG_XML = "<ThingDef>" + ("<x>filler</x>" * 2000) + "</ThingDef>"


@pytest.fixture
def indexed(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> RimWorldIndex:
    monkeypatch.setattr(index_module, "cache_home", lambda: tmp_path)
    subject = RimWorldIndex()
    db = subject.connection()
    try:
        cursor = db.execute(
            "INSERT INTO def(pack,def_type,def_name,inherit_name,parent_name,abstract,"
            "label,description,file_path,xml) VALUES(?,?,?,?,?,?,?,?,?,?)",
            ("Core", "ThingDef", "Steel", None, None, 0, "steel", "a metal", "Core/Defs/x.xml", BIG_XML),
        )
        db.execute(
            "INSERT INTO def_fts(rowid,def_name,label,description) VALUES(?,?,?,?)",
            (cursor.lastrowid, "Steel", "steel", "a metal"),
        )
        db.execute(
            "INSERT INTO symbol VALUES(?,?,?,?,?,?,?,?)",
            ("Verse.Pawn_X", "Pawn_X", "class", "Verse", "Verse/Pawn_X.cs", 1, 2, "class Pawn_X"),
        )
        db.execute(
            "INSERT INTO symbol VALUES(?,?,?,?,?,?,?,?)",
            ("Verse.PawnAX", "PawnAX", "class", "Verse", "Verse/PawnAX.cs", 1, 2, "class PawnAX"),
        )
        db.commit()
    finally:
        db.close()
    return subject


def test_search_defs_never_returns_raw_xml_by_default(indexed: RimWorldIndex) -> None:
    """xml 欄位可達數 MB，預設回傳會直接灌爆呼叫端的 context。"""
    rows = indexed.search_defs("steel")

    assert rows and "xml" not in rows[0]
    assert rows[0]["def_name"] == "Steel"


def test_search_defs_truncates_xml_when_requested(indexed: RimWorldIndex) -> None:
    rows = indexed.search_defs("steel", include_xml=True)

    assert rows[0]["xml_truncated"] is True
    assert len(str(rows[0]["xml"]).encode()) <= index_module.SEARCH_XML_BYTES


def test_read_def_returns_full_xml_within_limit(indexed: RimWorldIndex) -> None:
    rows = indexed.read_def("Steel")

    assert rows[0]["xml_truncated"] is False
    assert rows[0]["xml"] == BIG_XML


def test_fts_operator_characters_do_not_raise(indexed: RimWorldIndex) -> None:
    """未經處理的 " * ^ NEAR 會讓 FTS5 拋出看不懂的 OperationalError。"""
    for query in ('steel"', "NEAR", "^steel", "a:b", "*", "-steel"):
        indexed.search_defs(query)


def test_fts_match_keeps_prefix_search_and_quotes_terms() -> None:
    assert _fts_match("Steel*") == '"Steel"*'
    assert _fts_match('a"b') == '"a""b"'
    assert _fts_match("*") == ""


def test_like_wildcards_in_symbol_names_are_literal(indexed: RimWorldIndex) -> None:
    """未跳脫時 Pawn_X 的底線會當成萬用字元，連 PawnAX 也一起撈出來。"""
    found = {str(row["fqn"]) for row in indexed.read_symbol("Pawn_X")}

    assert found == {"Verse.Pawn_X"}


def test_like_literal_escapes_wildcards() -> None:
    assert _like_literal("a%b_c") == "a" + B + "%b" + B + "_c"
