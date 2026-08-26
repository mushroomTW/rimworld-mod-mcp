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
    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM source_file;
            INSERT INTO source_fts(source_fts) VALUES('rebuild');
            """;
        command.ExecuteNonQuery();
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
            find.Parameters.AddWithValue("$path", path);

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
            removeFts.Parameters.AddWithValue("$path", path);
            removeFts.Parameters.AddWithValue("$text", previousText!);
            removeFts.ExecuteNonQuery();

            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE source_file SET text = $text WHERE id = $id;";
            update.Parameters.AddWithValue("$text", text);
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
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$text", text);

            id = (long)insert.ExecuteScalar()!;
        }

        using var fts = connection.CreateCommand();
        fts.CommandText = "INSERT INTO source_fts (rowid, path, text) VALUES ($rowid, $path, $text);";
        fts.Parameters.AddWithValue("$rowid", id);
        fts.Parameters.AddWithValue("$path", path);
        fts.Parameters.AddWithValue("$text", text);
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

    public static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_file;";
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

    internal static string SourceKindText(DefReferenceSource source) => source switch
    {
        DefReferenceSource.DefXml => "def_xml",
        DefReferenceSource.GameSource => "game_source",
        _ => "mod_source",
    };
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
