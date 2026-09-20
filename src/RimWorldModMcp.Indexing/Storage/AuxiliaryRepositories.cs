using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>索引的中繼資料（指紋、統計、狀態旗標）。</summary>
public static class IndexMetaRepository
{
    public static void Set(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO index_meta (key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public static string? Get(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }
}

/// <summary>反編譯後的原始碼。存在資料庫裡而不是落地成上萬個檔案。</summary>
public static class SourceFileRepository
{
    private const string PathParam = "$path";
    private const string TextParam = "$text";

    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM source_file;
            INSERT INTO source_fts(source_fts) VALUES('rebuild');
            -- 各組件「已完整索引」的標記跟著內容一起清，否則續跑會跳過已經空掉的組件。
            DELETE FROM index_meta WHERE key LIKE 'source\_done:%' ESCAPE '\';
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>單一組件鍵底下的檔案數。</summary>
    public static long CountAssembly(SqliteConnection connection, string assembly)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_file WHERE assembly = $assembly;";
        command.Parameters.AddWithValue("$assembly", assembly);
        return (long)command.ExecuteScalar()!;
    }

    public static void Insert(SqliteConnection connection, string assembly, string path, string text)
    {
        // FTS5 虛擬表不支援 UPSERT——`ON CONFLICT` 會直接拋
        // 「UPSERT not implemented for virtual table」。而且 external content 表
        // 對同一個 rowid 重複插入不會取代，而是留下舊內容的幽靈索引項
        // （integrity-check 也抓不到）。所以更新既有列時必須先用 'delete'
        // 指令、帶著「舊值」把舊索引項移掉，再插入新值。
        long id;
        string? previousText = null;

        using (var find = connection.CreateCommand())
        {
            find.CommandText = "SELECT id, text FROM source_file WHERE assembly = $assembly AND path = $path;";
            find.Parameters.AddWithValue("$assembly", assembly);
            find.Parameters.AddWithValue(PathParam, path);

            using var reader = find.ExecuteReader();
            id = reader.Read() ? reader.GetInt64(0) : -1;

            if (id >= 0)
            {
                previousText = reader.GetString(1);
            }
        }

        if (id >= 0)
        {
            using var removeFts = connection.CreateCommand();
            removeFts.CommandText = """
                INSERT INTO source_fts (source_fts, rowid, path, text) VALUES ('delete', $rowid, $path, $text);
                """;
            removeFts.Parameters.AddWithValue("$rowid", id);
            removeFts.Parameters.AddWithValue(PathParam, path);
            removeFts.Parameters.AddWithValue(TextParam, previousText!);
            removeFts.ExecuteNonQuery();

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE source_file SET text = $text WHERE id = $id;";
            update.Parameters.AddWithValue(TextParam, text);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        else
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO source_file (assembly, path, text) VALUES ($assembly, $path, $text)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("$assembly", assembly);
            insert.Parameters.AddWithValue(PathParam, path);
            insert.Parameters.AddWithValue(TextParam, text);

            id = (long)insert.ExecuteScalar()!;
        }

        using var fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO source_fts (rowid, path, text) VALUES ($rowid, $path, $text);";
        fts.Parameters.AddWithValue("$rowid", id);
        fts.Parameters.AddWithValue(PathParam, path);
        fts.Parameters.AddWithValue(TextParam, text);
        fts.ExecuteNonQuery();
    }

    /// <summary>
    /// 刪除指定組件前綴的所有原始碼列，並同步移除 FTS 索引項。
    /// external content 表必須先對每一列發 'delete' 指令再刪內容列，
    /// 順序反過來會留下孤兒索引項。
    /// </summary>
    public static void DeleteWhereAssemblyLike(SqliteConnection connection, string assemblyLike)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_fts (source_fts, rowid, path, text)
            SELECT 'delete', id, path, text FROM source_file WHERE assembly LIKE $like ESCAPE '\';
            DELETE FROM source_file WHERE assembly LIKE $like ESCAPE '\';
            """;
        command.Parameters.AddWithValue("$like", assemblyLike);
        command.ExecuteNonQuery();
    }

    /// <summary>讀出一個反編譯檔的完整內容；鍵不存在時回傳 <c>null</c>。</summary>
    public static string? Read(SqliteConnection connection, string assembly, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM source_file WHERE assembly = $assembly AND path = $path;";
        command.Parameters.AddWithValue("$assembly", assembly);
        command.Parameters.AddWithValue("$path", path);
        return command.ExecuteScalar() as string;
    }

    public static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_file;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>只算遊戲本體的檔案，不含 <c>mod:</c> 前綴的 Mod 組件。</summary>
    public static long CountGame(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_file WHERE assembly NOT LIKE 'mod:%';";
        return (long)command.ExecuteScalar()!;
    }
}

/// <summary>Def 被引用的位置。</summary>
public static class DefReferenceRepository
{
    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM def_reference;";
        command.ExecuteNonQuery();
    }

    public static void Insert(SqliteConnection connection, IEnumerable<DefReference> references)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO def_reference (def_name, file_path, line, source_kind, context, confidence)
            VALUES ($name, $path, $line, $kind, $context, $confidence)
            ON CONFLICT DO NOTHING;
            """;

        var name = command.CreateParameter("$name");
        var path = command.CreateParameter("$path");
        var line = command.CreateParameter("$line");
        var kind = command.CreateParameter("$kind");
        var context = command.CreateParameter("$context");
        var confidence = command.CreateParameter("$confidence");

        foreach (var reference in references)
        {
            name.Value = reference.DefName;
            path.Value = reference.FilePath;
            line.Value = reference.Line;
            kind.Value = SourceKindText(reference.SourceKind);
            context.Value = (object?)reference.Context ?? DBNull.Value;
            confidence.Value = reference.Confidence == DefReferenceConfidence.Exact ? "exact" : "heuristic";

            command.ExecuteNonQuery();
        }
    }

    /// <summary>查出某個 Def 被引用的所有位置。</summary>
    public static List<DefUsage> Find(SqliteConnection connection, string defName, string? sourceKind, int limit)
    {
        var sql = """
            SELECT def_name, file_path, line, source_kind, context, confidence
            FROM def_reference
            WHERE def_name = $name
            """;

        if (!string.IsNullOrEmpty(sourceKind))
        {
            sql += " AND source_kind = $kind";
        }

        // exact 要排在 heuristic 前面。不能寫 ORDER BY confidence——那只是靠
        // 'e' < 'h' 的字母順序巧合，加入第三種可信度就會靜默排錯。
        sql += " ORDER BY CASE confidence WHEN 'exact' THEN 0 ELSE 1 END, source_kind, file_path, line LIMIT $limit";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", defName);

        if (!string.IsNullOrEmpty(sourceKind))
        {
            command.Parameters.AddWithValue("$kind", sourceKind);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var results = new List<DefUsage>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new DefUsage
            {
                DefName = reader.GetString(0),
                FilePath = reader.GetString(1),
                Line = reader.GetInt32(2),
                SourceKind = reader.GetString(3),
                Context = reader.IsDBNull(4) ? null : reader.GetString(4),
                Confidence = reader.GetString(5),
            });
        }

        return results;
    }

    public static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM def_reference;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// 把暫存表裡的引用候選以 defName 名冊過濾後寫進 <c>def_reference</c>，
    /// 然後丟掉暫存表。
    ///
    /// <para>
    /// 候選在單趟掃描期間逐筆落進 TEMP 表而不是留在記憶體——形狀過濾後
    /// 仍是十萬到百萬等級，整批 List 會吃掉數百 MB；讓 SQLite 管記憶體，
    /// 過濾用一次 join 完成。
    /// </para>
    /// </summary>
    public static void ResolveCandidates(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO def_reference (def_name, file_path, line, source_kind, context, confidence)
            SELECT c.def_name, c.file_path, c.line, 'def_xml', c.context, 'exact'
            FROM def_ref_candidate c
            -- ParentName 指向的是抽象 Def 的 Name（inherit_name），那種 Def 沒有 def_name；
            -- 只 join def_name 的話，CollectCandidates 特地收的 ParentName 候選會全數被丟掉。
            WHERE EXISTS (SELECT 1 FROM def d WHERE d.def_name = c.def_name)
               OR EXISTS (SELECT 1 FROM def d WHERE d.inherit_name = c.def_name)
            ON CONFLICT DO NOTHING;
            DROP TABLE def_ref_candidate;
            """;
        command.ExecuteNonQuery();
    }

    internal static string SourceKindText(DefReferenceSource source) => source switch
    {
        DefReferenceSource.DefXml => "def_xml",
        DefReferenceSource.GameSource => "game_source",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
    };
}

/// <summary>
/// 引用候選的暫存表寫入器。建構時建立 TEMP 表並備妥 prepared statement，
/// 之後逐筆寫入；表的收尾（過濾＋清除）由
/// <see cref="DefReferenceRepository.ResolveCandidates"/> 負責。
/// </summary>
public sealed class DefReferenceCandidateWriter : IDisposable
{
    private readonly SqliteCommand _insert;
    private readonly SqliteParameter _name;
    private readonly SqliteParameter _path;
    private readonly SqliteParameter _line;
    private readonly SqliteParameter _context;

    public DefReferenceCandidateWriter(SqliteConnection connection)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                DROP TABLE IF EXISTS def_ref_candidate;
                CREATE TEMP TABLE def_ref_candidate (
                  def_name  TEXT NOT NULL,
                  file_path TEXT NOT NULL,
                  line      INTEGER NOT NULL,
                  context   TEXT NOT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        _insert = connection.CreateCommand();
        _insert.CommandText = """
            INSERT INTO def_ref_candidate (def_name, file_path, line, context)
            VALUES ($name, $path, $line, $context);
            """;

        _name = _insert.CreateParameter("$name");
        _path = _insert.CreateParameter("$path");
        _line = _insert.CreateParameter("$line");
        _context = _insert.CreateParameter("$context");
    }

    public void Write(Semantics.DefReferenceCandidate candidate)
    {
        _name.Value = candidate.DefName;
        _path.Value = candidate.FilePath;
        _line.Value = candidate.Line;
        _context.Value = candidate.Context;
        _insert.ExecuteNonQuery();
    }

    public void Dispose() => _insert.Dispose();
}

/// <summary>Def 引用的查詢結果。</summary>
public sealed record DefUsage
{
    public required string DefName { get; init; }

    public required string FilePath { get; init; }

    public required int Line { get; init; }

    public required string SourceKind { get; init; }

    public string? Context { get; init; }

    public required string Confidence { get; init; }
}
