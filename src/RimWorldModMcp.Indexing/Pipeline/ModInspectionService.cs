using System.Collections.Concurrent;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>
/// 一個已安裝 Mod 的組件資訊。<paramref name="Key"/> 是索引裡的組件鍵，可當作查詢的篩選值。
/// <paramref name="Error"/> 非 null 代表這顆組件無法載入（原生 DLL、損壞的檔案），
/// 該組件沒有被索引，但其餘組件不受影響。
/// </summary>
public sealed record ModAssemblyInfo(
    string Name,
    string Key,
    string Path,
    int SymbolCount,
    int SourceFileCount,
    bool FromCache,
    string? Error = null);

/// <summary>
/// Mod 原始碼搜尋結果。<paramref name="Indexing"/> 為 true 代表索引正在背景建立、
/// 本次沒有搜；<paramref name="Error"/> 是上一次背景索引失敗的原因；
/// <paramref name="BudgetExceeded"/> 為 true 代表搜尋用完時間／候選預算，結果不完整。
/// </summary>
public sealed record ModSearchResult(
    IReadOnlyList<SourceHit> Hits,
    bool SourceIndexed,
    bool Indexing,
    string? Error,
    bool BudgetExceeded = false,
    string? IncompleteReason = null);

/// <summary>
/// 按需檢視已安裝 Mod 的組件。
///
/// <para>
/// 反編譯結果與符號都存進同一個索引資料庫，用 mod:＜packageId＞:＜DLL 相對路徑＞
/// 當作組件鍵與遊戲本體的索引區隔開。這樣 Mod 的原始碼搜尋可以直接沿用
/// 既有的 FTS 索引，不需要另一套檔案快取。
/// </para>
/// </summary>
public sealed class ModInspectionService(
    IndexDatabase database,
    MemberDecompiler decompiler,
    CriticalSectionLock locks)
{
    private readonly AssemblySymbolReader _symbolReader = new();

    /// <summary>
    /// 等 <c>rebuild_index</c> 放掉寫鎖的上限。
    ///
    /// <para>
    /// 重建的 DB 階段（Def 掃描 + 符號寫入 + FTS 重建）可能數十秒，所以給得比
    /// SQLite 的 <c>busy_timeout</c>（5 秒）寬。逾時後拋出
    /// <see cref="RimWorldModMcp.Core.Locking.LockHeldException"/>，呼叫端必須把它
    /// 當成**可重試**的失敗——不是永久失敗。
    /// </para>
    /// </summary>
    private static readonly TimeSpan WriteLockWait = TimeSpan.FromSeconds(60);

    /// <summary>進行中的背景索引，鍵是 packageId；同一個 Mod 只會有一個。</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task>> _indexing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>背景索引失敗的原因。行程內狀態，重啟後消失；<see cref="Inspect"/> 成功會清掉。</summary>
    private readonly ConcurrentDictionary<string, string> _indexErrors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 找出 Mod 的所有組件並在必要時建立索引。
    ///
    /// <para>
    /// 遞迴掃描整個 Mod 目錄，因此 Assemblies/1.5/*.dll 這種常見的
    /// 多版本佈局也能抓到——Python 版只看 Assemblies/ 頂層而漏掉它們。
    /// Source/、bin/、obj/ 底下的 DLL 是原始碼專案與建置產物，不算 Mod 組件。
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

        // 這一段也是寫入，同樣要跟 rebuild_index 互斥。
        using (locks.Hold(IndexBuilder.WriteLockName, WriteLockWait, WriteLockDetails(packageId, "forget_stale")))
        {
            ForgetStale(connection, packageId, keys.Keys);
        }

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
            //
            // 兩者都逐組件容錯：Assemblies/ 底下混有原生程式庫或損壞的 DLL 是常態，
            // 一顆壞檔不該讓整個 Mod 的索引失敗（那正是 Lazy 毒化 bug 的放大途徑）。
            string? assemblyError = null;

            List<SymbolRecord> symbols;

            try
            {
                symbols = _symbolReader.Read(assembly)
                    .Select(s => s with { Assembly = key, AssemblyPath = assembly })
                    .ToList();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // 與 MemberDecompiler.TryCreate 同樣廣泛捕捉：輸入是使用者 Mod 目錄裡
                // 任意 .dll，截斷或畸形的檔案在 System.Reflection.Metadata 路徑上拋出的
                // 型別無法逐一列舉。逐一列舉會漏，漏掉的代價是例外逃出逐組件包覆，
                // 使整個 Mod 索引失敗並在背景路徑被記成永久錯誤。
                symbols = [];
                assemblyError = $"Could not read .NET metadata: {e.Message}";
            }

            var sources = decompiler
                .DecompileAll(assembly, onUnavailable: reason => assemblyError ??= reason)
                .ToList();

            var sourceCount = 0;

            // 只有這一段（毫秒級的 DB 寫入）需要跟 rebuild_index 互斥。反編譯在上面、
            // 在鎖外完成——把它包進鎖內會讓一次 rebuild 被卡住好幾分鐘。
            //
            // 共用 IndexBuilder.WriteLockName 是 H2 的核心修正：兩者若各用各的鎖，
            // 並行時落後的一方會在 busy_timeout 之後以 SQLITE_BUSY 失敗，
            // 而那個失敗會被記成永久失敗、不再自動重試。
            using var writeLock = locks.Hold(
                IndexBuilder.WriteLockName, WriteLockWait, WriteLockDetails(packageId, "index_assembly"));
            using var transaction = connection.BeginTransaction();

            ClearAssembly(connection, key);

            SymbolRepository.Insert(connection, symbols);

            foreach (var (path, text) in sources)
            {
                SourceFileRepository.Insert(connection, key, path, text);
                sourceCount++;
            }

            // 無法載入的組件仍然寫入 stamp：否則 IsIndexed 永遠回 false，
            // 每次 search_source 都會重新反編譯這個 Mod 的其餘組件（分鐘級）。
            // 一個非 .NET 組件本來就沒有可搜尋的 C# 內容，跳過它不會少任何命中。
            IndexMetaRepository.Set(connection, $"mod_stamp:{key}", stamp);

            transaction.Commit();

            results.Add(new ModAssemblyInfo(
                Path.GetFileNameWithoutExtension(assembly),
                key,
                assembly,
                symbols.Count,
                sourceCount,
                FromCache: false,
                assemblyError));
        }

        // 走到這裡就是成功，不論是背景還是 force 重試；上一次的失敗原因不該再擋搜尋。
        _indexErrors.TryRemove(packageId, out var previousError);

        return results;
    }

    /// <summary>
    /// 搜尋 Mod 原始碼；尚未索引時不同步反編譯，而是在背景啟動索引並立刻回報
    /// Indexing=true。大 Mod 反編譯要幾分鐘，同步做會撞上 MCP client 的呼叫逾時：
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

        if (!IsIndexed(packageId, modPath, assemblies) || HasStaleAssemblies(packageId, modPath, assemblies))
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
        var search = SourceQueryService.Search(
            connection, pattern, null, limit,
            assemblyLike: AssemblyLike(packageId, assemblyFilter));

        return new ModSearchResult(
            search.Hits,
            SourceIndexed: true,
            Indexing: false,
            Error: null,
            search.BudgetExceeded,
            search.IncompleteReason);
    }

    /// <summary>
    /// 索引是否含已刪除組件的殘留鍵。只讀，不取鎖；
    /// 有殘留即視為未索引，觸發背景重整（其內的 ForgetStale 會清掉幽靈版本）。
    /// </summary>
    private bool HasStaleAssemblies(string packageId, string modPath, IReadOnlyList<string> assemblies)
    {
        var live = assemblies
            .Select(a => AssemblyKey(packageId, modPath, a))
            .ToHashSet(StringComparer.Ordinal);

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT assembly FROM symbol WHERE assembly LIKE $like ESCAPE '\'
            UNION
            SELECT assembly FROM source_file WHERE assembly LIKE $like ESCAPE '\';
            """;
        command.Parameters.AddWithValue("$like", AssemblyLike(packageId, null));

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (!live.Contains(reader.GetString(0)))
            {
                return true;
            }
        }

        return false;
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
            catch (Microsoft.Data.Sqlite.SqliteException e) when (SqliteCorruption.IsCorrupt(e))
            {
                // 損毀不是這個 Mod 的問題，force 重試也救不回來；要指向真正的出路。
                _indexErrors[packageId] = $"{e.Message} The index database is corrupt; call rebuild_index, then search again.";
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) when (SqliteCorruption.IsTransientBusy(e))
            {
                // 寫鎖衝突是**暫時性**的：rebuild_index 正在寫，或另一個 Mod 正在提交。
                // 記成永久失敗會讓這個 Mod 從此不再自動重試——TrySearchSource 看到
                // _indexErrors 有值就直接回報 index_error，使用者只看到「索引失敗」，
                // 卻不知道再搜一次就好。不記錄，下一次搜尋自然會重試。
            }
            catch (LockHeldException)
            {
                // 等不到寫鎖（rebuild_index 的寫交易比 WriteLockWait 還久）。
                // 與 SQLITE_BUSY 同類：暫時性、重試即可，不能記成永久失敗。
                // 少了這一條，一個特別慢的重建會讓 Mod 索引從此不再自動重試。
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

    /// <summary>
    /// 忘掉所有背景索引的失敗原因。rebuild_index 重設資料庫之後呼叫：
    /// 留著的話，因損毀而失敗的 Mod 在資料庫已經修好後仍會被 TrySearchSource 擋下。
    /// </summary>
    public void ForgetErrors() => _indexErrors.Clear();

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

        transaction.Commit();
    }

    /// <summary>寫鎖的持有者資訊，寫進鎖檔供殘骸診斷用。</summary>
    private static Dictionary<string, string> WriteLockDetails(string packageId, string operation) => new()
    {
        ["operation"] = operation,
        ["package_id"] = packageId,
    };

    /// <summary>
    /// 把 packageId 轉成安全的鎖檔名：不同 Mod 互不阻塞，同 Mod 仍序列化。
    /// 鎖檔名僅用於 CriticalSectionLock 的檔名，需避開路徑分隔字元。
    /// 非 [a-z0-9._-] 字元一律壓成 _，理論上 foo/bar 與
    /// foo_bar 會撞到同一個鎖——但 packageId 規範本就是小寫加 ._-，
    /// 實務撞不到；撞到也只是把兩個 Mod 序列化，不影響正確性。
    /// 輸出不含分隔符，不會跳出鎖目錄（見 CriticalSectionLock.LockPath）。
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
            // 遊戲只從 Assemblies/ 目錄載入（根目錄、版本目錄、Common 或 loadFolders 指定的
            // 資料夾底下）。其他地方的 DLL——LastVersion/ 之類的備份、Source/ 底下的專案與
            // bin／obj 建置產物——遊戲根本不會載入，索引只是噪音、還會讓同一行在搜尋結果
            // 裡重複出現，並擋到使用者的 dotnet build。
            // Path.GetRelativePath 每檔只算一次，兩個判斷共用。
            .Select(path => (Path: path, Relative: Path.GetRelativePath(modPath, path)))
            .Where(p => IsUnderAssembliesFolder(p.Relative))
            .Where(p => !IsBuildTree(p.Relative))
            // Harmony 之類的相依函式庫不是 Mod 自己的程式碼，索引它們只是噪音。
            .Where(p => !IsKnownDependency(Path.GetFileNameWithoutExtension(p.Path)))
            .Select(p => p.Path)
            .Order(StringComparer.Ordinal)];
    }

    private static bool IsUnderAssembliesFolder(string relativePath)
        => HasDirectorySegment(relativePath, "Assemblies");

    private static bool IsBuildTree(string relativePath)
        => HasDirectorySegment(relativePath, "Source")
            || HasDirectorySegment(relativePath, "bin")
            || HasDirectorySegment(relativePath, "obj");

    /// <summary>相對路徑的目錄段（最後一段是檔名，不算）是否含指定名稱，大小寫不分。</summary>
    private static bool HasDirectorySegment(string relativePath, string segmentName)
        => relativePath
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Any(segment => segment.Equals(segmentName, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>清掉這個 Mod 底下不在 <paramref name="liveKeys"/> 裡的組件索引。</summary>
    private static void ForgetStale(Microsoft.Data.Sqlite.SqliteConnection connection, string packageId, IEnumerable<string> liveKeys)
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
            return;
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
