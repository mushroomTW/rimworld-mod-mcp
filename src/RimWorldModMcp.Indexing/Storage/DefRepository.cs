using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>
/// <c>def</c> 與 <c>def_fts</c> 的唯一存取點。
///
/// <para>
/// FTS5 的 external content 表有一條容易寫錯的不變式：清空時必須讓 FTS 先失效，
/// 否則會留下查得到卻回讀不到內容的孤兒索引項。把兩張表封在同一個類別裡、
/// 不對外暴露 raw SQL，呼叫端在型別層面就沒有寫錯順序的機會。
/// </para>
/// </summary>
public sealed class DefRepository
{
    /// <summary>搜尋結果附帶 XML 時的位元組上限。</summary>
    public const int SearchXmlBytes = 4096;

    /// <summary>讀取單一 Def 時的預設位元組上限。</summary>
    public const int ReadDefXmlBytes = 65536;

    /// <summary>清空所有 Def 與其全文索引。</summary>
    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // 先清內容表，再叫 FTS 從（現已為空的）內容表重建整個索引。
        // 這比「先刪 def_fts 再刪 def」更不容易寫錯，結果等價。
        command.CommandText = """
            DELETE FROM def;
            INSERT INTO def_fts(def_fts) VALUES('rebuild');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 批次寫入 Def。呼叫端負責開啟交易，並在全部寫完後呼叫 <see cref="RebuildFts"/>。
    ///
    /// <para>
    /// 這裡刻意不逐筆維護 FTS 索引。external content 表可以在內容全部就位之後
    /// 一次重建，省掉每一列額外的 <c>RETURNING id</c> 往返與一次 FTS 寫入——
    /// 十萬列的規模下差距是數量級的。
    /// </para>
    /// </summary>
    public static int Insert(SqliteConnection connection, IEnumerable<DefRecord> defs)
    {
        using var insertDef = connection.CreateCommand();
        insertDef.CommandText = """
            INSERT INTO def (pack, def_type, def_name, inherit_name, parent_name, abstract, label, description, file_path, xml)
            VALUES ($pack, $type, $name, $inherit, $parent, $abstract, $label, $description, $path, $xml);
            """;

        var pack = insertDef.CreateParameter("$pack");
        var type = insertDef.CreateParameter("$type");
        var name = insertDef.CreateParameter("$name");
        var inherit = insertDef.CreateParameter("$inherit");
        var parent = insertDef.CreateParameter("$parent");
        var isAbstract = insertDef.CreateParameter("$abstract");
        var label = insertDef.CreateParameter("$label");
        var description = insertDef.CreateParameter("$description");
        var path = insertDef.CreateParameter("$path");
        var xml = insertDef.CreateParameter("$xml");

        var count = 0;

        foreach (var def in defs)
        {
            pack.Value = def.Pack;
            type.Value = def.DefType;
            name.Value = (object?)def.DefName ?? DBNull.Value;
            inherit.Value = (object?)def.InheritName ?? DBNull.Value;
            parent.Value = (object?)def.ParentName ?? DBNull.Value;
            isAbstract.Value = def.Abstract ? 1 : 0;
            label.Value = def.Label;
            description.Value = def.Description;
            path.Value = def.FilePath;
            xml.Value = def.Xml;

            insertDef.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    /// <summary>
    /// 從內容表重建整個全文索引。批次寫入完成後呼叫一次。
    ///
    /// <para>
    /// FTS 索引的是 defName，抽象 Def 只有 Name 屬性而沒有 defName，
    /// 所以搜尋不到——除非命中 label 或 description。這是既有行為。
    /// </para>
    /// </summary>
    public static void RebuildFts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO def_fts(def_fts) VALUES('rebuild');";
        command.ExecuteNonQuery();
    }

    /// <summary>依關鍵字搜尋 Def。</summary>
    public static List<DefHit> Search(
        SqliteConnection connection,
        string query,
        string? defType,
        int limit,
        bool includeXml)
    {
        var match = FtsQuery.Match(query);

        var columns = "d.id, d.pack, d.def_type, d.def_name, d.inherit_name, d.parent_name, d.abstract, d.label, d.description, d.file_path"
            + (includeXml ? ", d.xml" : string.Empty);

        var sql = $"SELECT {columns} FROM def d";
        var conditions = new List<string>();

        // 沒有可用搜尋詞時整個跳過 FTS join，退化成「無條件回傳到上限」。
        if (match.Length > 0)
        {
            sql += " JOIN def_fts f ON f.rowid = d.id";
            conditions.Add("def_fts MATCH $match");
        }

        if (!string.IsNullOrEmpty(defType))
        {
            conditions.Add("d.def_type = $type");
        }

        if (conditions.Count > 0)
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        sql += " LIMIT $limit";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (match.Length > 0)
        {
            command.Parameters.AddWithValue("$match", match);
        }

        if (!string.IsNullOrEmpty(defType))
        {
            command.Parameters.AddWithValue("$type", defType);
        }

        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        return ReadHits(command, includeXml, SearchXmlBytes);
    }

    /// <summary>依 defName 或抽象 Def 的 Name 讀取完整定義。</summary>
    public static List<DefHit> Read(SqliteConnection connection, string defName, string? defType, int maxBytes)
    {
        var sql = """
            SELECT id, pack, def_type, def_name, inherit_name, parent_name, abstract, label, description, file_path, xml
            FROM def
            WHERE (def_name = $name OR inherit_name = $name)
            """;

        if (!string.IsNullOrEmpty(defType))
        {
            sql += " AND def_type = $type";
        }

        sql += " ORDER BY pack, file_path LIMIT 10";

        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", defName);

        if (!string.IsNullOrEmpty(defType))
        {
            command.Parameters.AddWithValue("$type", defType);
        }

        return ReadHits(command, includeXml: true, Utf8Text.Clamp(maxBytes, 1024, 262144));
    }

    public static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM def;";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>索引中出現過的所有具名 defName，供引用分析比對。</summary>
    public static HashSet<string> DefNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT def_name FROM def WHERE def_name IS NOT NULL AND def_name <> '';";

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static List<DefHit> ReadHits(SqliteCommand command, bool includeXml, int xmlBytes)
    {
        var results = new List<DefHit>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            string? xml = null;
            bool? truncated = null;

            if (includeXml)
            {
                xml = Utf8Text.Truncate(reader.GetString(10), xmlBytes, out var wasTruncated);
                truncated = wasTruncated;
            }

            results.Add(new DefHit
            {
                Id = reader.GetInt64(0),
                Pack = reader.GetString(1),
                DefType = reader.GetString(2),
                DefName = reader.IsDBNull(3) ? null : reader.GetString(3),
                InheritName = reader.IsDBNull(4) ? null : reader.GetString(4),
                ParentName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Abstract = reader.GetInt32(6) != 0,
                Label = reader.GetString(7),
                Description = reader.GetString(8),
                FilePath = reader.GetString(9),
                Xml = xml,
                XmlTruncated = truncated,
            });
        }

        return results;
    }
}

internal static class SqliteCommandExtensions
{
    /// <summary>建立並掛上一個具名參數，方便在批次寫入時重複設值。</summary>
    internal static SqliteParameter CreateParameter(this SqliteCommand command, string name)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
