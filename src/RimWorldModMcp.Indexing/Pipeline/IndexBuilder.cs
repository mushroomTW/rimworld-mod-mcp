using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Defs;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Semantics;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>一次索引建置的結果。</summary>
public sealed record IndexBuildResult(
    string? Fingerprint,
    int DefCount,
    int SymbolCount,
    long ReferenceCount,
    IReadOnlyList<string> Assemblies,
    long ElapsedMilliseconds);

/// <summary>索引目前的狀態。</summary>
/// <param name="Busy">
/// 有另一個寫者持有寫鎖，狀態未知。與 <paramref name="Healthy"/> 分開是必要的：
/// 回同一個 false 會讓呼叫端在重建進行中被叫去「再重建一次」。
/// </param>
public sealed record IndexStatus(
    bool Fresh,
    bool Healthy,
    bool Busy,
    string? Fingerprint,
    long DefCount,
    long SymbolCount,
    bool SourceIndexed,
    SourceIndexState SourceIndex);

/// <summary>
/// 原始碼全文索引的狀態。只給 SourceIndexed=false 一個布林的話，呼叫端分不出
/// 「還在建」「建失敗」「根本沒啟動」——這三種各有不同的正確反應（等、重建、報告）。
/// </summary>
/// <param name="Running">本行程內是否正在背景建立。</param>
/// <param name="IndexedFiles">已寫入的檔案數；完成後等於總數。</param>
/// <param name="Error">上一次建立失敗的原因（跨行程保存在 index_meta）。</param>
public sealed record SourceIndexState(bool Running, int IndexedFiles, string? Error);

/// <summary>
/// 建立第一層索引：Def XML 與 IL metadata 符號。
///
/// <para>
/// 這一層刻意不做反編譯，所以是秒級完成的。反編譯分成兩種：
/// 讀單一成員原始碼時隨用隨算，全專案反編譯則是背景或按需執行。
/// </para>
/// </summary>
public sealed class IndexBuilder(
    IndexDatabase database,
    RimWorldLocator locator,
    IndexFingerprint fingerprint,
    CriticalSectionLock locks,
    SourceIndexer sourceIndexer)
{
    /// <summary>
    /// 所有會寫入索引資料庫的工作共用的鎖名。
    ///
    /// <para>
    /// 重建與 Mod 索引都必須取得它。兩者若各用各的鎖，就會同時持有寫交易，
    /// 落後的一方在 <c>busy_timeout</c>（5 秒）之後以 <c>SQLITE_BUSY</c> 失敗——
    /// 而文件建議的流程正是「rebuild_index → search_source(package_id)」，
    /// 會直接踩到這個組合。
    /// </para>
    /// <para>
    /// 呼叫端的持有範圍必須盡量短（見 <see cref="ModInspectionService.Inspect"/>：
    /// 反編譯在鎖外完成，只有毫秒級的 DB 寫入在鎖內）。
    /// </para>
    /// </summary>
    public const string WriteLockName = "index";

    private readonly AssemblySymbolReader _symbolReader = new();

    /// <summary>清空第一層索引與反編譯原始碼。</summary>
    public static void ClearAll(SqliteConnection connection)
    {
        DefRepository.Clear(connection);
        SymbolRepository.Clear(connection);
        DefReferenceRepository.Clear(connection);
        SourceFileRepository.Clear(connection);

        // symbol 與 source_file 是整表清掉，已索引 Mod 的列也一起沒了；
        // stamp 留著的話下一次 Inspect 會誤判已快取，回零符號、零命中。
        ModInspectionService.ForgetAll(connection);
    }

    /// <summary>全量重建 Def 與符號索引。</summary>
    public IndexBuildResult Rebuild()
    {
        // 背景的原始碼索引若還在跑，它會在我們清空 source_file 之後繼續往裡寫、
        // 最後把 source_indexed 覆寫回 true——先叫停再動表。
        sourceIndexer.Cancel();

        try
        {
            return RebuildCore();
        }
        catch (SqliteException e) when (SqliteCorruption.IsCorrupt(e))
        {
            // 索引完全是衍生快取；資料庫損毀時，清表式重建連交易都開不起來。
            // Reset 會關閉連線池並一併移除 WAL/SHM，之後僅重試這一次，避免把
            // 磁碟或權限等非損毀問題藏在無限重試裡。
            database.Reset();
            return RebuildCore();
        }
    }

    private IndexBuildResult RebuildCore()
    {
        var paths = locator.Detect();

        if (paths.ManagedDir is null || paths.DataDir is null)
        {
            throw new DirectoryNotFoundException("RimWorld Managed or Data directory not found.");
        }

        using var _ = locks.Hold(WriteLockName, new Dictionary<string, string> { ["operation"] = "rebuild_index" });

        var stopwatch = Stopwatch.StartNew();
        var assemblies = GameAssemblies(paths.ManagedDir);

        // Data/ 只走訪一次：Def 掃描、引用候選與指紋計算共用同一份清單，
        // 兩萬個檔案不再走三遍、解析兩遍。
        var packs = DefFileWalker.Walk(paths.DataDir);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        ClearAll(connection);

        int defCount;

        using (var candidates = new DefReferenceCandidateWriter(connection))
        {
            defCount = DefRepository.Insert(connection, DefDataScan.Run(packs, candidates.Write));
        }

        var symbolCount = 0;
        foreach (var assembly in assemblies)
        {
            symbolCount += SymbolRepository.Insert(
                connection,
                _symbolReader.Read(assembly).Select(s => s with { AssemblyPath = assembly }));
        }

        // 內容全部就位之後才建全文索引，比逐筆維護快一個數量級。
        DefRepository.RebuildFts(connection);

        // 引用分析要等 Def 與符號都寫好才能比對。
        var knownDefNames = DefRepository.DefNames(connection);

        DefReferenceRepository.Insert(connection, DefReferenceAnalyzer.FromDefOfFields(connection, knownDefNames));
        DefReferenceRepository.ResolveCandidates(connection);

        var referenceCount = DefReferenceRepository.Count(connection);

        var computed = fingerprint.ComputeFrom(paths, packs);

        IndexMetaRepository.Set(connection, "fingerprint", computed ?? string.Empty);
        IndexMetaRepository.Set(connection, "def_count", defCount.ToString());
        IndexMetaRepository.Set(connection, "symbol_count", symbolCount.ToString());
        IndexMetaRepository.Set(connection, "assemblies", string.Join('|', assemblies.Select(Path.GetFileNameWithoutExtension)));
        IndexMetaRepository.Set(connection, "built_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
        IndexMetaRepository.Set(connection, "reference_count", referenceCount.ToString());
        IndexMetaRepository.Set(connection, "source_indexed", "false");

        // 換掉建置世代：其他行程仍在跑的背景原始碼索引據此放棄寫完成標記（見 SourceIndexer.GenerationKey）。
        IndexMetaRepository.Set(connection, SourceIndexer.GenerationKey, Guid.NewGuid().ToString("n"));

        transaction.Commit();
        stopwatch.Stop();

        return new IndexBuildResult(
            computed,
            defCount,
            symbolCount,
            referenceCount,
            [.. assemblies.Select(a => Path.GetFileNameWithoutExtension(a))],
            stopwatch.ElapsedMilliseconds);
    }

    /// <summary>回報索引是否仍與目前安裝一致。</summary>
    public IndexStatus Status()
    {
        var paths = locator.Detect();
        var current = fingerprint.Compute(paths);

        // 健康度一定要先查（獨立連線跑 quick_check）：不健康時資料表計數本身會拋例外。
        // 忙碌與損毀必須分開——它們的正確反應完全不同（稍後重試 vs. 重建）。
        // F11：不健康時不可再開連線讀資料表，否則 NOTADB／CORRUPT 逃出，
        // rimworld_status 無法按結構化契約回報 healthy=false。
        var health = database.Check();
        var busy = health == IndexDatabase.Health.Busy;
        var healthy = health == IndexDatabase.Health.Ok;

        if (!healthy)
        {
            return new IndexStatus(false, healthy, busy, null, 0, 0, false, new SourceIndexState(false, 0, null));
        }

        using var connection = database.Open();

        string? stored = null;

        try
        {
            stored = IndexMetaRepository.Get(connection, "fingerprint");
        }
        catch (SqliteException e) when (SqliteCorruption.IsTransientBusy(e))
        {
            // DELETE journal 模式下讀者也會被寫鎖擋住。狀態工具仍應該回答
            // 「有人在寫」而不是整個失敗，所以這裡吞掉並讓下面的 busy 說明原因。
        }
        catch (SqliteException e) when (SqliteCorruption.IsCorrupt(e))
        {
            // Check 通過後資料表仍可能損毀（競態）：降級為不健康而非拋錯。
            return CorruptStatus(null);
        }

        // 兩邊都必須是實際算得出來的值才談得上一致；
        // 偵測不到遊戲時 current 為 null，這種情況一律視為不新鮮。
        var fresh = current is not null
            && !string.IsNullOrEmpty(stored)
            && string.Equals(stored, current, StringComparison.Ordinal);

        try
        {
            return new IndexStatus(
                fresh,
                healthy,
                busy,
                stored,
                DefRepository.Count(connection),
                SymbolRepository.Count(connection),
                IndexMetaRepository.Get(connection, "source_indexed") == "true",
                SourceIndexState(connection));
        }
        catch (SqliteException e) when (SqliteCorruption.IsCorrupt(e))
        {
            return CorruptStatus(stored);
        }
    }

    /// <summary>損毀時的降級狀態：結構化回報不健康，不再依賴資料表內容。</summary>
    private static IndexStatus CorruptStatus(string? stored)
        => new(false, false, false, stored, 0, 0, false, new SourceIndexState(false, 0, null));

    private SourceIndexState SourceIndexState(SqliteConnection connection)
    {
        var progress = sourceIndexer.Progress;

        // 行程內的進度優先；沒有在跑時直接數資料表——被中斷的索引會留下部分檔案，
        // 只看完成時才寫的 source_file_count 會把「有一半」報成「零」。
        var indexedFiles = progress.Running
            ? progress.FilesIndexed
            : (int)SourceFileRepository.CountGame(connection);

        var storedError = IndexMetaRepository.Get(connection, "source_index_error");
        var error = progress.Error ?? (string.IsNullOrEmpty(storedError) ? null : storedError);

        return new SourceIndexState(progress.Running, indexedFiles, error);
    }

    /// <summary>
    /// 要索引哪些組件。
    ///
    /// <para>
    /// Python 版是硬編碼的 DLC 白名單，每次有新 DLC 上市都要改程式碼。
    /// 這裡改成掃目錄，凡是 Assembly-CSharp 開頭的都納入——
    /// RimWorld 的 DLC 內容都編進這些組件裡，Unity 與 BCL 組件則不是索引目標。
    /// </para>
    /// </summary>
    private static List<string> GameAssemblies(string managedDirectory)
        => [.. Directory.GetFiles(managedDirectory, "Assembly-CSharp*.dll").Order(StringComparer.Ordinal)];
}
