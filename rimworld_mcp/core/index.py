from __future__ import annotations

import hashlib
import importlib
import json
import os
import re
import shutil
import sqlite3
import subprocess
import xml.etree.ElementTree as etree
from collections.abc import Iterable
from contextlib import suppress
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from rimworld_mcp.core.locks import held
from rimworld_mcp.core.paths import cache_home, detect_rimworld


def _dotnet_environment() -> dict[str, str]:
    """允許已安裝的較新 .NET Runtime 執行 ILSpy 的 net8 工具。"""
    return {**os.environ, "DOTNET_ROLL_FORWARD": "Major"}


# def.xml 是整份 Def 的序列化 XML，單筆就可能上百 KB。搜尋結果預設不帶 xml，
# 需要完整內容時改用 read_def()，避免一次查詢就灌爆呼叫端的 context。
DEF_COLUMNS = (
    "id",
    "pack",
    "def_type",
    "def_name",
    "inherit_name",
    "parent_name",
    "abstract",
    "label",
    "description",
    "file_path",
)
SEARCH_XML_BYTES = 4096
READ_DEF_XML_BYTES = 65536
B = chr(92)  # 反斜線；用常數避免多層字串跳脫看不懂
ESCAPABLE = frozenset({B, "%", "_"})


def _fts_match(query: str) -> str:
    """把使用者輸入轉成純字面的 FTS5 查詢字串。

    FTS5 會把 " * ^ : - NEAR 等字元當成運算子，直接轉送使用者輸入會拋出
    sqlite3.OperationalError 而變成看不懂的工具錯誤。這裡把每個詞包成帶引號的
    字面詞（詞尾保留 * 作為前綴搜尋），隱含 AND 的行為不變，但語法錯誤消失。
    """
    terms: list[str] = []
    for token in query.split():
        prefix = token.endswith("*")
        body = (token[:-1] if prefix else token).replace('"', '""')
        if not body:
            continue
        terms.append(f'"{body}"*' if prefix else f'"{body}"')
    return " ".join(terms)


def _like_literal(value: str) -> str:
    """跳脫 LIKE 的萬用字元，讓 % 與 _ 以字面比對。"""
    return "".join(B + ch if ch in ESCAPABLE else ch for ch in value)


def _clip(text: str, limit: int) -> tuple[str, bool]:
    """以 UTF-8 位元組為單位截斷，回傳 (內容, 是否被截斷)。"""
    raw = text.encode("utf-8")
    if len(raw) <= limit:
        return text, False
    return raw[:limit].decode("utf-8", errors="ignore"), True


@dataclass(frozen=True, slots=True)
class Symbol:
    fqn: str
    short_name: str
    kind: str
    parent_fqn: str | None
    file_path: str
    start_line: int
    end_line: int
    signature: str


class RimWorldIndex:
    """索引路徑一律 lazy 求值。

    cache_home() 帶 ensure_exists=True 會建立目錄，若在 __init__ 內求值，
    光是 import rimworld_mcp.mcp_server 就會產生檔案系統副作用。
    """

    @property
    def root(self) -> Path:
        return cache_home() / "index"

    @property
    def source_root(self) -> Path:
        return self.root / "Source"

    @property
    def defs_root(self) -> Path:
        return self.root / "Defs"

    @property
    def db_path(self) -> Path:
        return self.root / "index.sqlite3"

    @property
    def meta_path(self) -> Path:
        return self.root / "meta.json"

    def connection(self) -> sqlite3.Connection:
        self.root.mkdir(parents=True, exist_ok=True)
        db = sqlite3.connect(self.db_path)
        db.row_factory = sqlite3.Row
        db.executescript(
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS def (
              id INTEGER PRIMARY KEY, pack TEXT NOT NULL, def_type TEXT NOT NULL,
              def_name TEXT, inherit_name TEXT, parent_name TEXT, abstract INTEGER NOT NULL,
              label TEXT NOT NULL, description TEXT NOT NULL, file_path TEXT NOT NULL, xml TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS def_fts USING fts5(def_name, label, description, content='def', content_rowid='id');
            CREATE TABLE IF NOT EXISTS symbol (
              fqn TEXT NOT NULL, short_name TEXT NOT NULL, kind TEXT NOT NULL, parent_fqn TEXT,
              file_path TEXT NOT NULL, start_line INTEGER NOT NULL, end_line INTEGER NOT NULL, signature TEXT NOT NULL,
              PRIMARY KEY(fqn, file_path, start_line)
            );
            CREATE TABLE IF NOT EXISTS def_reference (
              def_name TEXT NOT NULL, file_path TEXT NOT NULL, line INTEGER NOT NULL,
              PRIMARY KEY(def_name, file_path, line)
            );
            CREATE INDEX IF NOT EXISTS idx_symbol_short ON symbol(short_name);
            CREATE INDEX IF NOT EXISTS idx_def_name ON def(def_name);
            """
        )
        return db

    def status(self) -> dict[str, object]:
        paths = detect_rimworld()
        meta: dict[str, object] | None = None
        with suppress(OSError, json.JSONDecodeError):
            meta = json.loads(self.meta_path.read_text(encoding="utf-8"))
        return {"paths": paths.as_json(), "index": meta, "fresh": bool(meta and meta.get("fingerprint") == self.fingerprint())}

    def fingerprint(self) -> str | None:
        paths = detect_rimworld()
        if not paths.managed_dir or not paths.data_dir:
            return None
        digest = hashlib.sha256()
        version = (paths.install_root / "Version.txt") if paths.install_root else None
        for item in [version, *[paths.managed_dir / name for name in self._assemblies(paths.managed_dir)]]:
            if item and item.is_file():
                stat = item.stat()
                digest.update(f"{item.name}:{stat.st_size}:{stat.st_mtime_ns}".encode())
        for pack in sorted(paths.data_dir.iterdir()):
            if (pack / "Defs").is_dir():
                digest.update(pack.name.encode())
        return digest.hexdigest()

    @staticmethod
    def _assemblies(managed: Path) -> list[str]:
        names = ["Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll"]
        names.extend(["Royalty.dll", "Ideology.dll", "Biotech.dll", "Anomaly.dll", "Odyssey.dll"])
        return [name for name in names if (managed / name).is_file()]

    def setup_toolchain(self, confirm: bool) -> dict[str, str]:
        if not confirm:
            raise PermissionError("安裝 ILSpyCmd 需要 confirm=true。")
        project = Path(__file__).resolve().parents[2]
        run = subprocess.run(
            ["dotnet", "tool", "restore"],
            cwd=project,
            capture_output=True,
            text=True,
            env=_dotnet_environment(),
            encoding="utf-8",
            errors="replace",
            check=False,
        )
        if run.returncode:
            raise RuntimeError((run.stderr or run.stdout)[-2000:])
        return {"toolchain": "ilspycmd", "status": "ready"}

    def rebuild(self) -> dict[str, object]:
        with held("index", {"operation": "rebuild_index"}):
            return self._rebuild()

    def _rebuild(self) -> dict[str, object]:
        paths = detect_rimworld()
        if not paths.managed_dir or not paths.data_dir:
            raise FileNotFoundError("找不到 RimWorld 的 Managed 或 Data 目錄。")
        project = Path(__file__).resolve().parents[2]
        if not (project / ".store/ilspycmd").exists() and not (project / ".config/dotnet-tools.json").exists():
            raise RuntimeError("尚未準備 ILSpyCmd；請先呼叫 setup_toolchain(confirm=true)。")
        shutil.rmtree(self.source_root, ignore_errors=True)
        shutil.rmtree(self.defs_root, ignore_errors=True)
        self.source_root.mkdir(parents=True)
        self.defs_root.mkdir(parents=True)
        for name in self._assemblies(paths.managed_dir):
            assembly = paths.managed_dir / name
            output = self.source_root / assembly.stem
            run = subprocess.run(
                ["dotnet", "tool", "run", "ilspycmd", "--", str(assembly), "-p", "-o", str(output)],
                cwd=project,
                capture_output=True,
                text=True,
                env=_dotnet_environment(),
                encoding="utf-8",
                errors="replace",
                check=False,
            )
            if run.returncode and not any(output.rglob("*.cs")):
                raise RuntimeError(f"反編譯 {name} 失敗：{(run.stderr or run.stdout)[-1200:]}")
        db = self.connection()
        try:
            db.executescript("DELETE FROM def_fts; DELETE FROM def_reference; DELETE FROM symbol; DELETE FROM def;")
            def_names = self._index_defs(db, paths.data_dir)
            symbols = self._index_symbols(db, def_names)
            db.commit()
        finally:
            db.close()
        meta: dict[str, object] = {
            "fingerprint": self.fingerprint(),
            "def_count": len(def_names),
            "symbol_count": symbols,
        }
        self.meta_path.write_text(json.dumps(meta, indent=2), encoding="utf-8")
        return meta

    def _index_defs(self, db: sqlite3.Connection, data_dir: Path) -> set[str]:
        names: set[str] = set()
        for pack in data_dir.iterdir():
            source = pack / "Defs"
            if not source.is_dir():
                continue
            destination = self.defs_root / pack.name
            shutil.copytree(source, destination, dirs_exist_ok=True)
            for file in source.rglob("*.xml"):
                try:
                    root = etree.parse(file).getroot()
                except etree.ParseError:
                    continue
                if root.tag != "Defs":
                    continue
                for node in root:
                    def_name = (node.findtext("defName") or "").strip() or None
                    if def_name:
                        names.add(def_name)
                    xml = etree.tostring(node, encoding="unicode")
                    record = (pack.name, node.tag, def_name, node.attrib.get("Name"), node.attrib.get("ParentName"), int(node.attrib.get("Abstract", "false").lower() == "true"), (node.findtext("label") or "").strip(), (node.findtext("description") or "").strip(), str(file.relative_to(data_dir)).replace("\\", "/"), xml)
                    cursor = db.execute("INSERT INTO def(pack,def_type,def_name,inherit_name,parent_name,abstract,label,description,file_path,xml) VALUES(?,?,?,?,?,?,?,?,?,?)", record)
                    db.execute("INSERT INTO def_fts(rowid,def_name,label,description) VALUES(?,?,?,?)", (cursor.lastrowid, def_name or "", record[6], record[7]))
        return names

    def _index_symbols(self, db: sqlite3.Connection, def_names: set[str]) -> int:
        count = 0
        for file in self.source_root.rglob("*.cs"):
            text = file.read_text(encoding="utf-8", errors="replace")
            relative = str(file.relative_to(self.source_root)).replace("\\", "/")
            for symbol in extract_symbols(text, relative):
                db.execute("INSERT OR REPLACE INTO symbol VALUES(?,?,?,?,?,?,?,?)", (symbol.fqn, symbol.short_name, symbol.kind, symbol.parent_fqn, symbol.file_path, symbol.start_line, symbol.end_line, symbol.signature))
                count += 1
            for line_number, line in enumerate(text.splitlines(), start=1):
                for candidate in re.findall(r'"([A-Z][A-Za-z0-9_]{2,})"', line):
                    if candidate in def_names:
                        db.execute("INSERT OR IGNORE INTO def_reference VALUES(?,?,?)", (candidate, relative, line_number))
        return count

    def search_defs(
        self,
        query: str,
        def_type: str | None = None,
        limit: int = 25,
        include_xml: bool = False,
    ) -> list[dict[str, object]]:
        """搜尋已索引的 Def。預設不回傳 xml 欄位；需要完整定義時用 read_def()。"""
        columns = [f"d.{name}" for name in DEF_COLUMNS]
        if include_xml:
            columns.append("d.xml")
        sql = f"SELECT {', '.join(columns)} FROM def d"
        params: list[object] = []
        conditions: list[str] = []
        match = _fts_match(query)
        if match:
            sql += " JOIN def_fts f ON f.rowid=d.id"
            conditions.append("def_fts MATCH ?")
            params.append(match)
        if def_type:
            conditions.append("d.def_type=?")
            params.append(def_type)
        if conditions:
            sql += " WHERE " + " AND ".join(conditions)
        sql += " LIMIT ?"
        params.append(min(max(limit, 1), 200))
        db = self.connection()
        try:
            rows = db.execute(sql, params).fetchall()
        except sqlite3.OperationalError as exc:
            raise ValueError(f"Def 查詢失敗：{exc}") from exc
        finally:
            db.close()
        results: list[dict[str, object]] = []
        for row in rows:
            item = dict(row)
            if include_xml:
                item["xml"], item["xml_truncated"] = _clip(str(item["xml"]), SEARCH_XML_BYTES)
            results.append(item)
        return results

    def read_def(
        self,
        def_name: str,
        def_type: str | None = None,
        max_bytes: int = READ_DEF_XML_BYTES,
    ) -> list[dict[str, object]]:
        """依 defName（或抽象 Def 的 Name）取回完整 XML，並在指定上限處截斷。"""
        sql = "SELECT * FROM def WHERE (def_name=? OR inherit_name=?)"
        params: list[object] = [def_name, def_name]
        if def_type:
            sql += " AND def_type=?"
            params.append(def_type)
        sql += " ORDER BY pack, file_path LIMIT 10"
        db = self.connection()
        try:
            rows = db.execute(sql, params).fetchall()
        finally:
            db.close()
        limit = min(max(max_bytes, 1024), 262144)
        results: list[dict[str, object]] = []
        for row in rows:
            item = dict(row)
            item["xml"], item["xml_truncated"] = _clip(str(item["xml"]), limit)
            results.append(item)
        return results

    def inspect_mod(self, mod_dir: Path) -> list[dict[str, object]]:
        """按需反編譯 Mod DLL，並以檔案版本指紋管理本機快取。"""
        project = Path(__file__).resolve().parents[2]
        if not (project / ".config" / "dotnet-tools.json").is_file():
            raise RuntimeError("請先執行 setup_toolchain(confirm=true) 安裝專案私有的 ILSpyCmd。")
        results: list[dict[str, object]] = []
        for assembly in sorted((mod_dir / "Assemblies").glob("*.dll")):
            stat = assembly.stat()
            stamp = f"{assembly.resolve()}:{stat.st_size}:{stat.st_mtime_ns}"
            digest = hashlib.sha256(stamp.encode()).hexdigest()[:20]
            destination = cache_home() / "mods" / digest / assembly.stem
            marker = destination.parent / "source.json"
            if not marker.is_file():
                shutil.rmtree(destination.parent, ignore_errors=True)
                destination.mkdir(parents=True, exist_ok=True)
                run = subprocess.run(
                    [
                        "dotnet",
                        "tool",
                        "run",
                        "ilspycmd",
                        "--",
                        str(assembly),
                        "-p",
                        "-o",
                        str(destination),
                    ],
                    cwd=project,
                    env=_dotnet_environment(),
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    check=False,
                )
                if run.returncode and not any(destination.rglob("*.cs")):
                    detail = (run.stderr or run.stdout or "")[-1200:]
                    raise RuntimeError(f"反編譯 Mod 組件 {assembly.name} 失敗：{detail}")
                marker.write_text(json.dumps({"stamp": stamp}), encoding="utf-8")
            results.append(
                {
                    "assembly": assembly.name,
                    "cache": str(destination),
                    "source_files": sum(1 for _ in destination.rglob("*.cs")),
                }
            )
        return results

    def search_mod_source(
        self, mod_dir: Path, pattern: str, limit: int = 100
    ) -> list[dict[str, object]]:
        """按需反編譯後，在第三方 Mod 原始碼快取中搜尋。"""
        regex = re.compile(pattern, re.IGNORECASE)
        results: list[dict[str, object]] = []
        for assembly in self.inspect_mod(mod_dir):
            root = Path(str(assembly["cache"]))
            for source in root.rglob("*.cs"):
                for line_number, line in enumerate(
                    source.read_text(encoding="utf-8", errors="replace").splitlines(), 1
                ):
                    if regex.search(line):
                        results.append(
                            {
                                "assembly": assembly["assembly"],
                                "file": str(source.relative_to(root)).replace("\\", "/"),
                                "line": line_number,
                                "text": line[:1000],
                            }
                        )
                        if len(results) >= min(max(limit, 1), 800):
                            return results
        return results

    def read_symbol(self, name: str, limit_bytes: int = 4096) -> list[dict[str, object]]:
        db = self.connection()
        try:
            rows = db.execute(
                "SELECT * FROM symbol WHERE short_name=? OR fqn LIKE ? ESCAPE '\\' ORDER BY file_path,start_line LIMIT 20",
                (name, f"%{_like_literal(name)}%"),
            ).fetchall()
        finally:
            db.close()
        result: list[dict[str, object]] = []
        for row in rows:
            item = dict(row)
            source = self.source_root / str(item["file_path"])
            lines = source.read_text(encoding="utf-8", errors="replace").splitlines() if source.is_file() else []
            body = "\n".join(lines[int(item["start_line"]) - 1 : int(item["end_line"])])
            item["body"] = body.encode()[: min(max(limit_bytes, 256), 32768)].decode(errors="replace")
            result.append(item)
        return result

    def search_source(self, pattern: str, file_pattern: str = "*", limit: int = 200) -> list[dict[str, object]]:
        regex = re.compile(pattern, re.IGNORECASE)
        results: list[dict[str, object]] = []
        for root in (self.source_root, self.defs_root):
            for file in root.rglob(file_pattern):
                if not file.is_file():
                    continue
                for line_number, line in enumerate(file.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
                    if regex.search(line):
                        results.append({"file": str(file.relative_to(self.root)).replace("\\", "/"), "line": line_number, "text": line[:1000]})
                        if len(results) >= min(max(limit, 1), 800):
                            return results
        return results


def _tree_sitter_symbols(text: str, file_path: str) -> list[Symbol] | None:
    """在 optional binding 可用時，以 Tree-sitter 建立較可靠的 C# 符號表。"""
    try:
        tree_sitter = importlib.import_module("tree_sitter")
        csharp = importlib.import_module("tree_sitter_c_sharp")
        language = tree_sitter.Language(csharp.language())
        try:
            parser = tree_sitter.Parser(language)
        except TypeError:
            parser = tree_sitter.Parser()
            parser.language = language
        root = parser.parse(text.encode("utf-8")).root_node
    except (ImportError, AttributeError, TypeError, ValueError):
        return None

    kinds = {
        "class_declaration": "class",
        "struct_declaration": "struct",
        "interface_declaration": "interface",
        "enum_declaration": "enum",
        "record_declaration": "record",
        "delegate_declaration": "delegate",
        "method_declaration": "method",
        "constructor_declaration": "constructor",
        "property_declaration": "property",
    }
    lines = text.splitlines()
    symbols: list[Symbol] = []

    def name_of(node: Any) -> str | None:
        child = node.child_by_field_name("name")
        if child is None:
            return None
        return text[child.start_byte : child.end_byte]

    def visit(node: Any, parents: list[str]) -> None:
        kind = kinds.get(node.type)
        name = name_of(node)
        namespace = node.type == "namespace_declaration"
        if namespace and name:
            parents = [*parents, name]
        elif kind and name:
            parent = ".".join(parents) or None
            line = node.start_point[0] + 1
            signature = lines[line - 1].strip() if line <= len(lines) else name
            fqn = ".".join([*parents, name])
            symbols.append(Symbol(fqn, name, kind, parent, file_path, line, node.end_point[0] + 1, signature))
            if kind in {"class", "struct", "interface", "enum", "record"}:
                parents = [*parents, name]
        for child in node.children:
            visit(child, parents)

    visit(root, [])
    return symbols or None


def extract_symbols(text: str, file_path: str) -> Iterable[Symbol]:
    parsed = _tree_sitter_symbols(text, file_path)
    if parsed is not None:
        yield from parsed
        return
    namespace = ""
    stack: list[tuple[str, int]] = []
    type_re = re.compile(r"^\s*(?:public|private|internal|protected|static|abstract|sealed|partial|\s)*(class|struct|interface|enum|record|delegate)\s+(\w+)")
    method_re = re.compile(r"^\s*(?:public|private|internal|protected|static|virtual|override|async|sealed|extern|new|\s)+[\w<>\[\],.? ]+\s+(\w+)\s*\(")
    for number, line in enumerate(text.splitlines(), 1):
        ns = re.match(r"\s*namespace\s+([\w.]+)", line)
        if ns:
            namespace = ns.group(1)
        while stack and line.count("}") > line.count("{"):
            stack.pop()
        type_match = type_re.match(line)
        if type_match:
            name = type_match.group(2)
            parent = ".".join([namespace, *[entry[0] for entry in stack if entry[0]]]).strip(".") or None
            fqn = ".".join(filter(None, [parent, name]))
            yield Symbol(fqn, name, type_match.group(1), parent, file_path, number, number, line.strip())
            stack.append((name, line.count("{") - line.count("}")))
            continue
        method = method_re.match(line)
        if method and stack:
            parent = ".".join(filter(None, [namespace, *[entry[0] for entry in stack]]))
            name = method.group(1)
            yield Symbol(f"{parent}.{name}", name, "method", parent, file_path, number, number, line.strip())
