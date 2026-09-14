using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>一個已安裝 Mod 的組件資訊。<paramref name="Key"/> 是索引裡的組件鍵，可當作查詢的篩選值。</summary>
public sealed record ModAssemblyInfo(string Name, string Key, string Path, int SymbolCount, int SourceFileCount, bool FromCache);

/// <summary>
/// 按需檢視已安裝 Mod 的組件。
///
/// <para>
/// 反編譯結果與符號都存進同一個索引資料庫，用 <c>mod:&lt;packageId&gt;:&lt;DLL 相對路徑&gt;</c>
/// 當作組件鍵與遊戲本體的索引區隔開。這樣 Mod 的原始碼搜尋可以直接沿用
/// 既有的 FTS 索引，不需要另一套檔案快取。
/// </para>
/// </summary>
public sealed class ModInspectionService(
    IndexDatabase database,
    MemberDecompiler decompiler,
    CriticalSectionLock locks,
    SourceQueryService sourceQueries)
{
    private readonly AssemblySymbolReader _symbolReader = new();

    /// <summary>
    /// 找出 Mod 的所有組件並在必要時建立索引。
    ///
    /// <para>
    /// 遞迴掃描整個 Mod 目錄，因此 <c>Assemblies/1.5/*.dll</c> 這種常見的
    /// 多版本佈局也能抓到——Python 版只看 <c>Assemblies/</c> 頂層而漏掉它們。
    /// </para>
    /// </summary>
    public IReadOnlyList<ModAssemblyInfo> Inspect(string packageId, string modPath, bool force = false)
    {
        var assemblies = FindAssemblies(modPath);

        // 依 packageId 分鎖：不同 Mod 的反編譯互不阻塞，大 Mod（Framework 等）
        // 長時間佔用時，小 Mod 的查詢仍可並行；同一個 Mod 的併發仍序列化，
        // 避免同時反編譯並寫入同一組鍵。
        using var _ = locks.Hold(LockName(packageId), new Dictionary<string, string> { ["package_id"] = packageId });

        var results = new List<ModAssemblyInfo>(assemblies.Count);
        using var connection = database.Open();

        // Mod 更新後被移除（或改名、搬到別的版本目錄）的 DLL，索引列不會自己消失；
        // 每次檢視都先把不屬於目前組件集合的鍵清掉，查詢結果才不會混進幽靈版本。
        var keys = assemblies.ToDictionary(a => AssemblyKey(packageId, modPath, a), StringComparer.Ordinal);
        var reindexed = ForgetStale(connection, packageId, keys.Keys);

        foreach (var (key, assembly) in keys)
        {
            var stamp = MemberDecompiler.Stamp(assembly);
            var cached = IndexMetaRepository.Get(connection, $"mod_stamp:{key}");

            if (!force && cached == stamp)
            {
                results.Add(new ModAssemblyInfo(
                    Path.GetFileNameWithoutExtension(assembly),
                    key,
                    assembly,
                    CountSymbols(connection, key),
                    CountSourceFiles(connection, key),
                    FromCache: true));

                continue;
            }

            // 反編譯與符號讀取是分鐘級的 CPU/IO 工作，先在交易外算完，
            // 再用短交易只包 DB 寫入。否則分鎖後兩個 Mod 並行索引時，
            // 先進者長時間持有寫交易，後進者超過 busy_timeout（5s）即 SQLITE_BUSY。
            var symbols = _symbolReader.Read(assembly)
                .Select(s => s with { Assembly = key, AssemblyPath = assembly })
                .ToList();

            var sources = decompiler.DecompileAll(assembly).ToList();
            var sourceCount = 0;

            using var transaction = connection.BeginTransaction();

            ClearAssembly(connection, key);

            SymbolRepository.Insert(connection, symbols);

            foreach (var (path, text) in sources)
            {
                SourceFileRepository.Insert(connection, key, path, text);
                sourceCount++;
            }

            IndexMetaRepository.Set(connection, $"mod_stamp:{key}", stamp);

            transaction.Commit();
            reindexed = true;

            results.Add(new ModAssemblyInfo(
                Path.GetFileNameWithoutExtension(assembly),
                key,
                assembly,
                symbols.Count,
                sourceCount,
                FromCache: false));
        }

        // 'rebuild' 是對整個 symbol 內容表（含遊戲本體數十萬列）重建索引，
        // 一次 Inspect 只能做一次——放進 per-assembly 迴圈的話，
        // 一個有五個 DLL 的 Mod 會觸發五次全庫重建。
        if (reindexed)
        {
            SymbolRepository.RebuildFts(connection);
        }

        return results;
    }

    /// <summary>
    /// 搜尋一個已安裝 Mod 的原始碼，必要時先建立索引。
    /// <paramref name="assemblyFilter"/> 是對組件鍵的子字串比對（例如 <c>1.6/</c>），
    /// 用來在多版本 Mod 裡只看其中一份 DLL。
    /// </summary>
    public (IReadOnlyList<SourceHit> Hits, bool SourceIndexed) SearchSource(
        string packageId, string modPath, string pattern, int limit, string? assemblyFilter = null)
    {
        var inspected = Inspect(packageId, modPath);

        using var connection = database.Open();

        // 組件前綴必須推進 SQL 過濾（見 SourceQueryService.Candidates 的說明），
        // 在記憶體裡事後過濾的話，遊戲本體的命中會把候選名額全部吃掉。
        var hits = sourceQueries.Search(
            connection, pattern, null, limit,
            assemblyLike: AssemblyLike(packageId, assemblyFilter));

        // XML-only Mod 沒有任何組件；回報 false 讓呼叫端知道零命中的原因。
        var indexed = inspected.Any(a => a.SourceFileCount > 0);

        return (hits, indexed);
    }

    /// <summary>清除一個 Mod 的所有索引資料。</summary>
    public void Forget(string packageId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var prefix = $"mod:{packageId}:";
        var like = FtsQuery.LikeLiteral(prefix) + "%";

        // 原始碼列交給會同步 FTS 的刪除；絕不能呼叫 SourceFileRepository.Clear——
        // 那是無條件全刪，會把 Core 加所有 DLC 的反編譯結果一併炸掉，
        // 使用者得付出重跑幾分鐘全文索引的代價。
        SourceFileRepository.DeleteWhereAssemblyLike(connection, like);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM symbol WHERE assembly LIKE $prefix ESCAPE '\';
                DELETE FROM index_meta WHERE key LIKE $metaPrefix ESCAPE '\';
                """;
            command.Parameters.AddWithValue("$prefix", like);
            command.Parameters.AddWithValue("$metaPrefix", "mod\\_stamp:" + FtsQuery.LikeLiteral(prefix) + "%");
            command.ExecuteNonQuery();
        }

        SymbolRepository.RebuildFts(connection);

        transaction.Commit();
    }

    /// <summary>
    /// 把 packageId 轉成安全的鎖檔名：不同 Mod 互不阻塞，同 Mod 仍序列化。
    /// 鎖檔名僅用於 <c>CriticalSectionLock</c> 的檔名，需避開路徑分隔字元。
    /// 非 <c>[a-z0-9._-]</c> 字元一律壓成 <c>_</c>，理論上 <c>foo/bar</c> 與
    /// <c>foo_bar</c> 會撞到同一個鎖——但 packageId 規範本就是小寫加 <c>._-</c>，
    /// 實務撞不到；撞到也只是把兩個 Mod 序列化，不影響正確性。
    /// 輸出不含分隔符，不會跳出鎖目錄（見 <c>CriticalSectionLock.LockPath</c>）。
    /// </summary>
    public static string LockName(string packageId)
    {
        var builder = new System.Text.StringBuilder(packageId.Length + 10);
        builder.Append("mod-index-");

        foreach (var c in packageId.Trim().ToLowerInvariant())
        {
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'
                ? c
                : '_');
        }

        return builder.ToString();
    }

    /// <summary>遞迴找出 Mod 目錄下所有 .NET 組件。</summary>
    private static List<string> FindAssemblies(string modPath)
    {
        if (!Directory.Exists(modPath))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(modPath, "*.dll", SearchOption.AllDirectories)
            // Harmony 之類的相依函式庫不是 Mod 自己的程式碼，索引它們只是噪音。
            .Where(path => !IsKnownDependency(Path.GetFileNameWithoutExtension(path)))
            .Order()];
    }

    private static bool IsKnownDependency(string name)
        => name is "0Harmony" or "HarmonyLib" or "Newtonsoft.Json"
            || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把 Mod 的組件鍵組成 LIKE 樣式：固定前綴加上可選的子字串篩選。
    /// </summary>
    public static string AssemblyLike(string packageId, string? assemblyFilter)
    {
        var like = FtsQuery.LikeLiteral($"mod:{packageId}:") + "%";

        return string.IsNullOrEmpty(assemblyFilter)
            ? like
            : like + FtsQuery.LikeLiteral(assemblyFilter) + "%";
    }

    private static string AssemblyKey(string packageId, string modPath, string assemblyPath)
    {
        // 同一個 Mod 常在不同版本目錄下放同名組件（1.5/Assemblies/X.dll、1.6/Assemblies/X.dll），
        // 用 DLL 在 Mod 目錄下的相對路徑當鍵，一眼就能看出是哪個版本，
        // 也能直接拿 "1.6/" 這種片段當篩選條件。
        var relative = Path.GetRelativePath(modPath, assemblyPath).Replace('\\', '/');
        return $"mod:{packageId}:{relative}";
    }

    /// <summary>清掉這個 Mod 底下不在 <paramref name="liveKeys"/> 裡的組件索引。回傳是否有清掉任何東西。</summary>
    private static bool ForgetStale(Microsoft.Data.Sqlite.SqliteConnection connection, string packageId, IEnumerable<string> liveKeys)
    {
        var live = liveKeys.ToHashSet(StringComparer.Ordinal);
        var stale = new HashSet<string>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT assembly FROM symbol WHERE assembly LIKE $like ESCAPE '\'
                UNION
                SELECT assembly FROM source_file WHERE assembly LIKE $like ESCAPE '\';
                """;
            command.Parameters.AddWithValue("$like", AssemblyLike(packageId, null));

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var key = reader.GetString(0);

                if (!live.Contains(key))
                {
                    stale.Add(key);
                }
            }
        }

        if (stale.Count == 0)
        {
            return false;
        }

        using var transaction = connection.BeginTransaction();

        foreach (var key in stale)
        {
            ClearAssembly(connection, key);

            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM index_meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", $"mod_stamp:{key}");
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    private static void ClearAssembly(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
    {
        // source_file 的刪除必須同步 FTS 索引項，交給 repository 的專用方法。
        SourceFileRepository.DeleteWhereAssemblyLike(connection, FtsQuery.LikeLiteral(key));

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM symbol WHERE assembly = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static int CountSymbols(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
        => CountWhere(connection, "SELECT COUNT(*) FROM symbol WHERE assembly = $key;", key);

    private static int CountSourceFiles(Microsoft.Data.Sqlite.SqliteConnection connection, string key)
        => CountWhere(connection, "SELECT COUNT(*) FROM source_file WHERE assembly = $key;", key);

    private static int CountWhere(Microsoft.Data.Sqlite.SqliteConnection connection, string sql, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
