using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary><c>symbol</c> 與 <c>symbol_fts</c> 的唯一存取點。</summary>
public static class SymbolRepository
{
    private const string AssemblyParam = "$assembly";

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

        var assembly = insert.CreateParameter(AssemblyParam);
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

    /// <summary>
    /// 查出符號：先以 FQN 或短名精確比對，有命中就只回這些（型別排在成員前、遊戲本體排在 Mod 前）；
    /// 一筆都沒有才退回 FQN 子字串比對。
    /// 兩者混在一起回的話，查 ThingDef 會拿到上千筆成員與 ThingDefCount 之類的雜訊，
    /// 呼叫端要的那一個型別反而被 limit 擠掉。
    /// <paramref name="assemblyLike"/> 是對組件鍵的 LIKE 樣式；多版本 Mod 的同一個類別
    /// 會在每份 DLL 各出現一次，靠它才能只看其中一份。
    /// </summary>
    public static List<SymbolHit> Read(SqliteConnection connection, string name, int limit, string? assemblyLike = null)
    {
        using var exact = connection.CreateCommand();
        exact.CommandText = """
            SELECT assembly, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static, assembly_path
            FROM symbol
            WHERE (fqn = $name OR short_name = $name)
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\')
            ORDER BY (fqn = $name) DESC, (kind IN ('Class', 'Struct', 'Interface', 'Enum', 'Delegate')) DESC, (assembly LIKE 'mod:%'), fqn
            LIMIT $limit;
            """;

        exact.Parameters.AddWithValue("$name", name);
        exact.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        exact.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        var hits = ReadHits(exact);

        if (hits.Count > 0)
        {
            return hits;
        }

        using var partial = connection.CreateCommand();
        partial.CommandText = """
            SELECT assembly, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static, assembly_path
            FROM symbol
            WHERE fqn LIKE $like ESCAPE '\'
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\')
            ORDER BY fqn
            LIMIT $limit;
            """;

        partial.Parameters.AddWithValue("$like", $"%{FtsQuery.LikeLiteral(name)}%");
        partial.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        partial.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        return ReadHits(partial);
    }

    /// <summary>
    /// 只符合 FQN 子字串、但不是精確命中的符號數。<see cref="Read"/> 有精確命中時略過這些，
    /// 這個數字讓呼叫端知道還有多少沒看到。
    /// </summary>
    public static int CountPartial(SqliteConnection connection, string name, string? assemblyLike = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM symbol
            WHERE fqn LIKE $like ESCAPE '\'
              AND fqn <> $name AND short_name <> $name
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\');
            """;

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$like", $"%{FtsQuery.LikeLiteral(name)}%");
        command.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        return (int)(long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// 列出直接隸屬於 <paramref name="parentFqn"/> 的符號：給 namespace 時得到其中的頂層型別，
    /// 給型別時得到它的成員與巢狀型別。這是「不知道關鍵字也能瀏覽」的入口。
    /// </summary>
    public static List<SymbolHit> Children(
        SqliteConnection connection, string parentFqn, string? kind, string? assemblyLike, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT assembly, fqn, short_name, kind, parent_fqn, metadata_token, signature, base_chain, interfaces, accessibility, is_static, assembly_path
            FROM symbol
            WHERE parent_fqn = $parent
              AND ($kind IS NULL OR kind = $kind)
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\')
            ORDER BY kind, fqn
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$parent", parentFqn);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        return ReadHits(command);
    }

    /// <summary>
    /// 列出 <paramref name="parentNamespace"/> 底下一層的子 namespace（空字串代表根）。
    /// 型別符號的 parent_fqn 就是它的 namespace，所以從型別的 parent 反推即可，
    /// 不需要另外存 namespace 表。
    /// </summary>
    public static List<string> ChildNamespaces(SqliteConnection connection, string parentNamespace, string? assemblyLike)
    {
        var prefix = parentNamespace.Length == 0 ? "" : parentNamespace + ".";

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT parent_fqn
            FROM symbol
            WHERE kind IN ('Class', 'Struct', 'Interface', 'Enum', 'Delegate')
              -- 巢狀型別的 fqn 是 Outer+Inner，它的 parent 是型別不是 namespace，要排除。
              AND fqn NOT LIKE '%+%'
              AND parent_fqn LIKE $like ESCAPE '\'
              AND parent_fqn <> $parent
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\');
            """;

        command.Parameters.AddWithValue("$like", FtsQuery.LikeLiteral(prefix) + "%");
        command.Parameters.AddWithValue("$parent", parentNamespace);
        command.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        var children = new SortedSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            // 只取緊接在 prefix 後的那一段：RimTalk.AI.Clients 對 RimTalk 而言是 RimTalk.AI。
            var rest = reader.GetString(0)[prefix.Length..];
            var dot = rest.IndexOf('.');
            children.Add(prefix + (dot < 0 ? rest : rest[..dot]));
        }

        return [.. children];
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
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        return ReadHits(command);
    }

    /// <summary>
    /// 零命中時的「你是不是要找」：把名稱最後一段依大小寫切成詞，
    /// 各自對 short_name 做子字串比對。LLM 呼叫端常記錯一半的名字
    /// （ResolutionUtility → Resolution / Utility），這比整段 LIKE 有用得多。
    /// 型別優先於成員，短的 fqn 優先。
    /// </summary>
    public static List<string> Suggest(SqliteConnection connection, string name, int limit)
    {
        var tokens = CamelCaseTokens(name);

        if (tokens.Count == 0)
        {
            return [];
        }

        var clauses = new List<string>(tokens.Count);
        using var command = connection.CreateCommand();

        for (var i = 0; i < tokens.Count; i++)
        {
            clauses.Add($"short_name LIKE $t{i} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$t{i}", $"%{FtsQuery.LikeLiteral(tokens[i])}%");
        }

        command.CommandText = $"""
            SELECT fqn
            FROM symbol
            WHERE {string.Join(" OR ", clauses)}
            ORDER BY (kind IN ('Class', 'Struct', 'Interface', 'Enum', 'Delegate')) DESC, length(fqn), fqn
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));

        var results = new List<string>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    /// <summary>
    /// 取名稱最後一段（去掉 namespace 與 <c>()</c>），依大小寫邊界切詞，
    /// 只留長度 ≥ 3 的詞、最長的兩個。
    /// </summary>
    private static List<string> CamelCaseTokens(string name)
    {
        var last = name;
        var paren = last.IndexOf('(');

        if (paren >= 0)
        {
            last = last[..paren];
        }

        var dot = last.LastIndexOf('.');

        if (dot >= 0)
        {
            last = last[(dot + 1)..];
        }

        var tokens = new List<string>();
        var start = 0;

        for (var i = 1; i <= last.Length; i++)
        {
            var boundary = i == last.Length
                || (char.IsUpper(last[i]) && !char.IsUpper(last[i - 1]))
                || !char.IsLetterOrDigit(last[i]);

            if (!boundary)
            {
                continue;
            }

            var token = last[start..i].Trim('_');

            if (token.Length >= 3)
            {
                tokens.Add(token);
            }

            start = char.IsLetterOrDigit(last[i == last.Length ? i - 1 : i]) ? i : i + 1;
        }

        return [.. tokens.OrderByDescending(t => t.Length).Take(2)];
    }

    /// <summary>
    /// <paramref name="parent"/> 是否是索引裡存在的 namespace 或型別。
    /// 空字串是根，一律存在。用來把「型別不存在」和「型別沒有成員」分開回報。
    /// </summary>
    public static bool ParentExists(SqliteConnection connection, string parent, string? assemblyLike)
    {
        if (parent.Length == 0)
        {
            return true;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM symbol
            WHERE (fqn = $parent OR parent_fqn = $parent OR parent_fqn LIKE $prefix ESCAPE '\')
              AND ($assembly IS NULL OR assembly LIKE $assembly ESCAPE '\')
            LIMIT 1;
            """;

        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$prefix", FtsQuery.LikeLiteral(parent) + ".%");
        command.Parameters.AddWithValue(AssemblyParam, (object?)assemblyLike ?? DBNull.Value);

        return command.ExecuteScalar() is not null;
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
