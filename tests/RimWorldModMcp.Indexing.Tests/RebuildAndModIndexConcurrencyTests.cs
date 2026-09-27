using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 需要本機安裝 RimWorld 的測試。沒有遊戲時**略過**（不是靜默通過）——
/// 這一類測試要跑真實的 Def 掃描與十萬筆符號索引，CI 上沒有遊戲。
/// </summary>
public sealed class RequiresGameFactAttribute : FactAttribute
{
    public RequiresGameFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_TEST_GAME") != "1")
        {
            Skip = "需要本機 RimWorld：設 RIMWORLD_MOD_MCP_TEST_GAME=1 才會執行。";
        }
    }
}

/// <summary>
/// rebuild_index 與 Mod 索引並行時的行為。
///
/// <para>
/// 這是 H2 的端到端驗證。真實規模下 <c>RebuildCore</c> 會持有單一寫交易
/// **十幾秒**（本機實測：13,809 Defs／103,499 符號／18.1 秒），而文件建議的流程
/// 正是「rebuild_index → search_source(package_id)」——兩者重疊是常態而不是特例。
/// </para>
/// <para>
/// 修正前：兩者各用各的鎖，Mod 索引在 <c>busy_timeout</c>（5 秒）之後以
/// <c>SQLITE_BUSY</c> 失敗，而且那個失敗被記成**永久**錯誤，之後不再自動重試。
/// </para>
/// </summary>
public sealed class RebuildAndModIndexConcurrencyTests : IDisposable
{
    private const string ModSource = """
        namespace Widgets
        {
            public class Widget
            {
                public void TryStartJob() { }
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-concurrency-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly StoreDirectories _store;
    private readonly IndexDatabase _database;
    private readonly RimWorldLocator _locator = new();
    private readonly CriticalSectionLock _locks;
    private readonly MemberDecompiler _decompiler;
    private readonly ModInspectionService _service;
    private readonly IndexBuilder _builder;

    public RebuildAndModIndexConcurrencyTests()
    {
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(_store);
        _locks = new CriticalSectionLock(_store, new ProcessHost());
        _decompiler = new MemberDecompiler(_locator);
        _service = new ModInspectionService(_database, _decompiler, _locks);
        _builder = new IndexBuilder(
            _database, _locator, new IndexFingerprint(), _locks, new SourceIndexer(_database, _locator));
    }

    public void Dispose()
    {
        _decompiler.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (!Directory.Exists(_root))
        {
            return;
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 測試失敗時背景索引可能還在跑，暫存目錄留給作業系統清。
        }
    }

    /// <summary>
    /// 在 rebuild 的寫交易進行中提交 Mod 索引：必須「等」而不是失敗，
    /// 而且兩者完成後 Mod 的索引必須完整、可搜尋。
    /// </summary>
    [RequiresGameFact]
    public void ModIndexCommitWaitsForAnInFlightRebuild()
    {
        var modPath = EmitMod();

        var rebuild = Task.Run(_builder.Rebuild);

        // 等 rebuild 真的拿到寫鎖——否則這個測試會退化成「兩個工作根本沒重疊」而假通過。
        Assert.True(
            WaitUntil(() => File.Exists(LockFile(IndexBuilder.WriteLockName)), TimeSpan.FromSeconds(30)),
            "rebuild 應該在 30 秒內取得寫鎖");

        var inspect = Task.Run(() => _service.Inspect("pkg", modPath));

        Assert.True(Task.WaitAll([rebuild, inspect], TimeSpan.FromMinutes(5)), "兩個工作都應該在 5 分鐘內完成");
        Assert.True(rebuild.IsCompletedSuccessfully, $"rebuild 失敗：{rebuild.Exception}");
        Assert.True(inspect.IsCompletedSuccessfully, $"Mod 索引不該因為 rebuild 而失敗：{inspect.Exception}");

        Assert.All(inspect.Result, a => Assert.Null(a.Error));

        // rebuild 的 ClearAll 會清掉所有 Mod 的列與 stamp；Mod 索引既然排在它後面提交，
        // 結果就必須存活下來。
        var search = _service.TrySearchSource("pkg", modPath, "TryStartJob", 10);

        Assert.True(search.SourceIndexed);
        Assert.Null(search.Error);
        Assert.NotEmpty(search.Hits);
    }

    /// <summary>
    /// 暫時性的寫鎖衝突**不能**變成永久錯誤。
    ///
    /// <para>
    /// 這裡刻意用另一條連線直接握住 DB 寫鎖（不經過檔案鎖），讓 Mod 索引過得了
    /// 檔案鎖卻在 DB 層吃到 <c>SQLITE_BUSY</c>——也就是修正前會「永久失敗」的那條路徑。
    /// </para>
    /// <para>
    /// 斷言刻意寫成「契約層級」而不是去戳內部狀態：衝突持續期間，<c>TrySearchSource</c>
    /// 必須一直回報 <c>Indexing=true</c> 且 <c>Error=null</c>（還在重試）；
    /// 修正前會在第一次 BUSY 之後變成 <c>Error</c> 非 null 且永久不再重試。
    /// </para>
    /// </summary>
    [RequiresGameFact]
    public async Task TransientWriteLockConflictDoesNotBecomeAPermanentError()
    {
        var modPath = EmitMod();

        using var blocker = _database.Open();
        using var blockerTransaction = blocker.BeginTransaction();

        // BeginTransaction 是 deferred 的，要先寫一筆才真的取得寫鎖。
        IndexMetaRepository.Set(blocker, "blocker", "1");

        Assert.True(_service.TrySearchSource("pkg", modPath, "TryStartJob", 10).Indexing);

        // 至少跨過一輪 busy_timeout（5 秒），確保真的發生過一次寫鎖衝突。
        await Task.Delay(TimeSpan.FromSeconds(8));

        var duringConflict = _service.TrySearchSource("pkg", modPath, "TryStartJob", 10);

        Assert.Null(duringConflict.Error);

        blockerTransaction.Rollback();

        // 放掉寫鎖後必須能自動重試成功——使用者不必知道要下 force。
        Assert.True(
            WaitUntil(
                () => _service.TrySearchSource("pkg", modPath, "TryStartJob", 10) is { SourceIndexed: true },
                TimeSpan.FromMinutes(3)),
            "寫鎖放掉之後應該自動重試成功");

        var search = _service.TrySearchSource("pkg", modPath, "TryStartJob", 10);

        Assert.Null(search.Error);
        Assert.NotEmpty(search.Hits);
    }

    /// <summary>把測試用的 Mod 放進 Assemblies/（遊戲只從那裡載入）。</summary>
    private string EmitMod()
    {
        var modPath = Path.Combine(_root, "mod");
        SyntheticAssembly.Emit(ModSource, "Widget", Path.Combine(modPath, "Assemblies"));

        return modPath;
    }

    private string LockFile(string name) => Path.Combine(_store.LocksDirectory, name + ".json");

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
