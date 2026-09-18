using Microsoft.Data.Sqlite;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>索引資料庫的結構定義。</summary>
public static class IndexSchema
{
    /// <summary>
    /// 結構版本。與資料庫裡的 <c>user_version</c> 不符時整個丟掉重建——
    /// 索引全部是衍生資料，重建不會損失任何使用者內容，所以不需要 migration 機制。
    /// </summary>
    public const int Version = 2;

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS def (
          id           INTEGER PRIMARY KEY,
          pack         TEXT NOT NULL,
          def_type     TEXT NOT NULL,
          def_name     TEXT,
          inherit_name TEXT,
          parent_name  TEXT,
          abstract     INTEGER NOT NULL,
          label        TEXT NOT NULL,
          description  TEXT NOT NULL,
          file_path    TEXT NOT NULL,
          xml          TEXT NOT NULL
        );

        -- external content 表：本身不存原文，查詢時回讀 def。
        -- 因此清空必須用 'rebuild' 指令或先刪 def_fts 再刪 def，順序反了會留下孤兒索引項。
        -- 這個不變式由 DefRepository 封裝，呼叫端拿不到 raw SQL。
        CREATE VIRTUAL TABLE IF NOT EXISTS def_fts
          USING fts5(def_name, label, description, content='def', content_rowid='id');

        CREATE INDEX IF NOT EXISTS idx_def_name ON def(def_name);
        CREATE INDEX IF NOT EXISTS idx_def_type ON def(def_type);

        CREATE TABLE IF NOT EXISTS symbol (
          id             INTEGER PRIMARY KEY,
          assembly       TEXT NOT NULL,
          -- 組件檔案的實際路徑。Mod 組件的 assembly 是 mod:pkg:Name:hash 形式的
          -- 索引鍵，拼不回檔案位置；反編譯入口需要真實路徑。
          assembly_path  TEXT,
          fqn            TEXT NOT NULL,
          short_name     TEXT NOT NULL,
          kind           TEXT NOT NULL,
          parent_fqn     TEXT,
          -- 反編譯單一成員的入口。舊版靠 (file_path, start_line) 定位，
          -- 改讀 metadata 之後定位基準換成 token。
          metadata_token INTEGER NOT NULL,
          signature      TEXT NOT NULL,
          base_chain     TEXT,
          interfaces     TEXT,
          accessibility  TEXT NOT NULL,
          is_static      INTEGER NOT NULL,
          -- metadata_token 在單一組件內已唯一。把 fqn 也放進鍵反而會讓
          -- 「同 token 但 fqn 算錯」的兩筆都寫得進去，遮蔽 bug 而非防止重複。
          UNIQUE(assembly, metadata_token)
        );

        CREATE INDEX IF NOT EXISTS idx_symbol_short ON symbol(short_name);
        CREATE INDEX IF NOT EXISTS idx_symbol_parent ON symbol(parent_fqn);

        CREATE VIRTUAL TABLE IF NOT EXISTS symbol_fts
          USING fts5(fqn, short_name, signature, content='symbol', content_rowid='id');

        -- 反編譯後的原始碼存在資料庫裡，不落地成上萬個檔案。
        CREATE TABLE IF NOT EXISTS source_file (
          id       INTEGER PRIMARY KEY,
          assembly TEXT NOT NULL,
          path     TEXT NOT NULL,
          text     TEXT NOT NULL,
          UNIQUE(assembly, path)
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS source_fts
          USING fts5(path, text, content='source_file', content_rowid='id');

        CREATE TABLE IF NOT EXISTS def_reference (
          def_name    TEXT NOT NULL,
          file_path   TEXT NOT NULL,
          line        INTEGER NOT NULL,
          -- 'def_xml' | 'game_source'
          source_kind TEXT NOT NULL,
          -- 命中的語境，例如 '<costList><Steel>' 或 'RimWorld.ThingDefOf.Steel'
          context     TEXT,
          -- 'exact'（語意解析）| 'heuristic'（字串比對）
          confidence  TEXT NOT NULL,
          PRIMARY KEY(def_name, file_path, line, source_kind)
        );

        CREATE INDEX IF NOT EXISTS idx_def_reference_name ON def_reference(def_name);

        -- 索引建立當下的來源指紋與統計，取代舊版另外存的 meta.json。
        CREATE TABLE IF NOT EXISTS index_meta (
          key   TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );
        """;

    internal static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        command.ExecuteNonQuery();
    }
}
