using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>驗證衍生索引檔損毀時，重建作業能自行建立乾淨資料庫。</summary>
// 兩個類別都改寫行程共用的 RIMWORLD_MOD_MCP_GAME_PATH，不能平行跑。
[Collection("GamePathEnvironment")]
public sealed class IndexBuilderRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousGamePath;

    public IndexBuilderRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-index-recovery-" + Guid.NewGuid().ToString("n")[..12]);
        _previousGamePath = Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", _previousGamePath);
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RebuildResetsAMalformedDerivedDatabaseAndRetriesOnce()
    {
        var install = Path.Combine(_root, "RimWorld");
        // Managed 目錄的位置依平台不同；CI 的這個測試專案跑在 Ubuntu 上。
        Directory.CreateDirectory(Path.Combine(install, OperatingSystem.IsWindows()
            ? Path.Combine("RimWorldWin64_Data", "Managed")
            : OperatingSystem.IsMacOS()
                ? Path.Combine("RimWorldMac.app", "Contents", "Resources", "Data", "Managed")
                : Path.Combine("RimWorldLinux_Data", "Managed")));
        Directory.CreateDirectory(Path.Combine(install, "Data", "Core", "Defs"));
        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", install);

        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        var database = new IndexDatabase(store);
        using (database.Open())
        {
        }

        SqliteConnection.ClearAllPools();
        using (var file = new FileStream(database.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.SetLength(100);
        }

        var locator = new RimWorldLocator();
        var sourceIndexer = new SourceIndexer(database, locator);
        var builder = new IndexBuilder(
            database,
            locator,
            new IndexFingerprint(),
            new CriticalSectionLock(store, new AlwaysAliveProcessHost()),
            sourceIndexer);

        var result = builder.Rebuild();

        Assert.Equal(0, result.DefCount);
        Assert.Equal(0, result.SymbolCount);
        using var connection = database.Open();
        Assert.Equal(0, DefRepository.Count(connection));
    }

    private sealed class AlwaysAliveProcessHost : IProcessHost
    {
        public bool IsAlive(int processId) => true;

        public bool Terminate(int processId, DateTime? expectedStartUtc = null) => true;

        public DateTime? StartTimeUtc(int processId) => DateTime.UtcNow;
    }
}
