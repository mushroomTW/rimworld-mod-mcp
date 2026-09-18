using System.Collections.Concurrent;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>一個已安裝 Mod 的組件資訊。<paramref name="Key"/> 是索引裡的組件鍵，可當作查詢的篩選值。</summary>
public sealed record ModAssemblyInfo(string Name, string Key, string Path, int SymbolCount, int SourceFileCount, bool FromCache);

/// <summary>
/// Mod 原始碼搜尋結果。<paramref name="Indexing"/> 為 true 代表索引正在背景建立、
/// 本次沒有搜；<paramref name="Error"/> 是上一次背景索引失敗的原因。
/// </summary>
public sealed record ModSearchResult(IReadOnlyList<SourceHit> Hits, bool SourceIndexed, bool Indexing, string? Error);

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

    /// <summary>進行中的背景索引，鍵是 packageId；同一個 Mod 只會有一個。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task>> _indexing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>背景索引失敗的原因。行程內狀態，重啟後消失；<see cref="Inspect"/> 成功會清掉。</summary>
    private readonly ConcurrentDictionary<string, string> _indexErrors = new(StringComparer.OrdinalIgnoreCase);

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

        // 走到這裡就是成功，不論是背景還是 force 重試；上一次的失敗原因不該再擋搜尋。
        _indexErrors.TryRemove(packageId, out var previousError);

        return results;
    }

    /// <summary>
    /// 搜尋 Mod 原始碼；尚未索引時不同步反編譯，而是在背景啟動索引並立刻回報
    /// <c>Indexing=true</c>。大 Mod 反編譯要幾分鐘，同步做會撞上 MCP client 的呼叫逾時：
    /// client 收到逾時錯誤、server 端卻還在跑，下一次呼叫又卡在鎖上。
    /// </summary>
    public ModSearchResult TrySearchSource(
        string packageId, string modPath, string pattern, int limit, string? assemblyFilter = null)
    {
        var assemblies = FindAssemblies(modPath);

        if (assemblies.Count == 0)
        {
            // XML-only Mod：沒東西可索引，回 false 讓呼叫端知道零命中的原因。
            return new ModSearchResult([], SourceIndexed: false, Indexing: false, Error: null);
        }

        if (!IsIndexed(packageId, modPath, assemblies))
        {
            if (_indexErrors.TryGetValue(packageId, out var error))
            {
                // 上一次已經失敗：不自動重跑數分鐘的反編譯，否則 agent 每次輪詢都
                // 觸發一次必定失敗的工作。要重試請走 Inspect(force: true)。
                return new ModSearchResult([], SourceIndexed: false, Indexing: false, Error: error);
            }

            var running = StartIndexing(packageId, modPath);

            return new ModSearchResult([], SourceIndexed: false, Indexing: running, Error: null);
        }

        using var connection = database.Open();
        var hits = sourceQueries.Search(
            connection, pattern, null, limit,
            assemblyLike: AssemblyLike(packageId, assemblyFilter));

        return new ModSearchResult(hits, SourceIndexed: true, Indexing: false, Error: null);
    }

    /// <summary>所有組件的 stamp 都與快取一致才算已索引。只讀，不取鎖。</summary>
    private bool IsIndexed(string packageId, string modPath, IReadOnlyList<string> assemblies)
    {
        using var connection = database.Open();

        foreach (var assembly in assemblies)
        {
            var key = AssemblyKey(packageId, modPath, assembly);

            if (IndexMetaRepository.Get(connection, $"mod_stamp:{key}") != MemberDecompiler.Stamp(assembly))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 啟動背景索引；已在跑就不重複啟動。回傳 true 代表現在有工作在跑。
    /// 失敗原因記進 <see cref="_indexErrors"/>，下一次 TrySearchSource 會回報，
    /// 但不會自動重試（見 TrySearchSource）。
    /// </summary>
    private bool StartIndexing(string packageId, string modPath)
    {
        // 用 Lazy 把 Task.Run 延後到條目插入之後：直接在 valueFactory 裡 Task.Run 的話，
        // 極快的工作可能在 GetOrAdd 插入前就跑完 finally 的 TryRemove（此時無條目、
        // no-op），之後插入的已完成 Task 就永遠留在字典裡，Mod 更新後再也不會重索引。
        var work = _indexing.GetOrAdd(packageId, key => new Lazy<Task>(() => Task.Run(() =>
        {
            try
            {
                Inspect(packageId, modPath);
            }
            catch (Exception e)
            {
                _indexErrors[packageId] = e.Message;
            }
            finally
            {
                _indexing.TryRemove(packageId, out _);
            }
        })));

        _ = work.Value;
        return true;
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

    /// <summary>忘掉所有 Mod 的索引 stamp；給整表清空後呼叫，讓下一次檢視重新反編譯。</summary>
    public static void ForgetAll(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM index_meta WHERE key LIKE 'mod_stamp:%';";
        command.ExecuteNonQuery();
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
