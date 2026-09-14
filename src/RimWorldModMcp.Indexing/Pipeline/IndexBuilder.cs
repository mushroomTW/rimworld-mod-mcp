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
public sealed record IndexStatus(
    bool Fresh,
    string? Fingerprint,
    long DefCount,
    long SymbolCount,
    bool SourceIndexed);

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
    private readonly AssemblySymbolReader _symbolReader = new();

    /// <summary>全量重建 Def 與符號索引。</summary>
    public IndexBuildResult Rebuild()
    {
        var paths = locator.Detect();

        if (paths.ManagedDir is null || paths.DataDir is null)
        {
            throw new DirectoryNotFoundException("RimWorld Managed or Data directory not found.");
        }

        // 背景的原始碼索引若還在跑，它會在我們清空 source_file 之後繼續往裡寫、
        // 最後把 source_indexed 覆寫回 true——先叫停再動表。
        sourceIndexer.Cancel();

        using var _ = locks.Hold("index", new Dictionary<string, string> { ["operation"] = "rebuild_index" });

        var stopwatch = Stopwatch.StartNew();
        var assemblies = GameAssemblies(paths.ManagedDir);

        // Data/ 只走訪一次：Def 掃描、引用候選與指紋計算共用同一份清單，
        // 兩萬個檔案不再走三遍、解析兩遍。
        var packs = DefFileWalker.Walk(paths.DataDir);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        DefRepository.Clear(connection);
        SymbolRepository.Clear(connection);
        DefReferenceRepository.Clear(connection);
        SourceFileRepository.Clear(connection);

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
        SymbolRepository.RebuildFts(connection);

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

        using var connection = database.Open();

        var stored = IndexMetaRepository.Get(connection, "fingerprint");

        // 兩邊都必須是實際算得出來的值才談得上一致；
        // 偵測不到遊戲時 current 為 null，這種情況一律視為不新鮮。
        var fresh = current is not null
            && !string.IsNullOrEmpty(stored)
            && string.Equals(stored, current, StringComparison.Ordinal);

        return new IndexStatus(
            fresh,
            stored,
            DefRepository.Count(connection),
            SymbolRepository.Count(connection),
            IndexMetaRepository.Get(connection, "source_indexed") == "true");
    }

    /// <summary>
    /// 要索引哪些組件。
    ///
    /// <para>
    /// Python 版是硬編碼的 DLC 白名單，每次有新 DLC 上市都要改程式碼。
    /// 這裡改成掃目錄，凡是 <c>Assembly-CSharp</c> 開頭的都納入——
    /// RimWorld 的 DLC 內容都編進這些組件裡，Unity 與 BCL 組件則不是索引目標。
    /// </para>
    /// </summary>
    private static List<string> GameAssemblies(string managedDirectory)
        => [.. Directory.GetFiles(managedDirectory, "Assembly-CSharp*.dll").Order()];
}
