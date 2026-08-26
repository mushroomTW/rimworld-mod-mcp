using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary><c>symbol</c> 與 <c>symbol_fts</c> 的唯一存取點。</summary>
public sealed class SymbolRepository
{
    public static void Clear(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM symbol;
            INSERT INTO symbol_fts(symbol_fts) VALUES('rebuild');
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 批次寫入符號。呼叫端負責開啟交易，並在全部寫完後呼叫 <see cref="RebuildFts"/>。
    /// 與 <see cref="DefRepository.Insert"/> 同樣的理由：FTS 一次重建比逐筆維護快得多。
    /// </summary>
    public static int Insert(SqliteConnection connection, IEnumerable<SymbolRecord> symbols)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO symbol (assembly, assembly_path, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static)
            VALUES ($assembly, $path, $fqn, $short, $kind, $parent, $token, $signature, $base, $interfaces, $access, $static)
            ON CONFLICT(assembly, metadata_token) DO NOTHING;
            """;

        var assembly = insert.CreateParameter("$assembly");
        var assemblyPath = insert.CreateParameter("$path");
        var fqn = insert.CreateParameter("$fqn");
        var shortName = insert.CreateParameter("$short");
        var kind = insert.CreateParameter("$kind");
        var parent = insert.CreateParameter("$parent");
        var token = insert.CreateParameter("$token");
        var signature = insert.CreateParameter("$signature");
        var baseChain = insert.CreateParameter("$base");
        var interfaces = insert.CreateParameter("$interfaces");
        var access = insert.CreateParameter("$access");
        var isStatic = insert.CreateParameter("$static");

        var count = 0;

        foreach (var symbol in symbols)
        {
            assembly.Value = symbol.Assembly;
            assemblyPath.Value = (object?)symbol.AssemblyPath ?? DBNull.Value;
            fqn.Value = symbol.Fqn;
            shortName.Value = symbol.ShortName;
            kind.Value = symbol.Kind.ToString();
            parent.Value = (object?)symbol.ParentFqn ?? DBNull.Value;
            token.Value = symbol.MetadataToken;
            signature.Value = symbol.Signature;
            baseChain.Value = (object?)symbol.BaseChain ?? DBNull.Value;
            interfaces.Value = (object?)symbol.Interfaces ?? DBNull.Value;
            access.Value = symbol.Accessibility;
            isStatic.Value = symbol.IsStatic ? 1 : 0;

            // 衝突時 DO NOTHING 會回報 0 列受影響，那代表這筆已經存在，不重複計數。
            count += insert.ExecuteNonQuery();
        }

        return count;
    }

    /// <summary>從內容表重建整個全文索引。批次寫入完成後呼叫一次。</summary>
    public static void RebuildFts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO symbol_fts(symbol_fts) VALUES('rebuild');";
        command.ExecuteNonQuery();
    }

    /// <summary>依短名精確比對，或以 FQN 子字串比對，查出符號。</summary>
    public static List<SymbolHit> Read(SqliteConnection connection, string name, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT assembly, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static, assembly_path
            FROM symbol
            WHERE short_name = $name OR fqn LIKE $like ESCAPE '\'
            ORDER BY (short_name = $name) DESC, fqn
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$like", $"%{FtsQuery.LikeLiteral(name)}%");
        command.Parameters.AddWithValue("$limit", Utf8Text.Clamp(limit, 1, 100));

        return ReadHits(command);
    }

    private static List<SymbolHit> ReadHits(SqliteCommand command)
    {
        var results = new List<SymbolHit>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(new SymbolHit
            {
                Assembly = reader.GetString(0),
                Fqn = reader.GetString(1),
                ShortName = reader.GetString(2),
                Kind = reader.GetString(3),
                ParentFqn = reader.IsDBNull(4) ? null : reader.GetString(4),
                MetadataToken = reader.GetInt32(5),
                Signature = reader.GetString(6),
                BaseChain = reader.IsDBNull(7) ? [] : reader.GetString(7).Split('|'),
                Interfaces = reader.IsDBNull(8) ? [] : reader.GetString(8).Split('|'),
                Accessibility = reader.GetString(9),
                IsStatic = reader.GetInt32(10) != 0,
                AssemblyPath = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return results;
    }

    /// <summary>
    /// 找出所有繼承自指定型別的類別。
    /// 這是 Python 版答不出來的查詢——它的 parent 只有一層。
    /// </summary>
    public static List<SymbolHit> Descendants(SqliteConnection connection, string baseTypeFqn, int limit)
    {
        // base_chain 裡存的是去除泛型引數的定義名稱（見 AssemblySymbolReader.BaseChain），
        // 查詢輸入也做同樣的正規化，Verse.Comp<T> 與 Verse.Comp`1 都能查。
        var backtick = baseTypeFqn.IndexOf('`');
        var angle = baseTypeFqn.IndexOf('<');
        var cut = (backtick, angle) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => angle,
            (_, < 0) => backtick,
            _ => Math.Min(backtick, angle),
        };

        if (cut >= 0)
        {
            baseTypeFqn = baseTypeFqn[..cut];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT assembly, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static, assembly_path
            FROM symbol
            WHERE base_chain IS NOT NULL
              AND ('|' || base_chain || '|') LIKE $needle ESCAPE '\'
            ORDER BY fqn
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$needle", $"%|{FtsQuery.LikeLiteral(baseTypeFqn)}|%");
        command.Parameters.AddWithValue("$limit", Utf8Text.Clamp(limit, 1, 500));

        return ReadHits(command);
    }

    public static long Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM symbol;";
        return (long)command.ExecuteScalar()!;
    }
}

/// <summary>符號查詢結果。</summary>
public sealed record SymbolHit
{
    public required string Assembly { get; init; }

    /// <summary>組件檔案的實際路徑；反編譯時優先使用（Mod 組件只能靠它定位）。</summary>
    public string? AssemblyPath { get; init; }

    public required string Fqn { get; init; }

    public required string ShortName { get; init; }

    public required string Kind { get; init; }

    public string? ParentFqn { get; init; }

    public required int MetadataToken { get; init; }

    public required string Signature { get; init; }

    public required IReadOnlyList<string> BaseChain { get; init; }

    public required IReadOnlyList<string> Interfaces { get; init; }

    public required string Accessibility { get; init; }

    public required bool IsStatic { get; init; }

    /// <summary>原始碼節錄。只有在明確要求時才會反編譯填入。</summary>
    public string? Body { get; init; }

    /// <summary>節錄是否因位元組上限而被截斷。</summary>
    public bool? BodyTruncated { get; init; }
}
