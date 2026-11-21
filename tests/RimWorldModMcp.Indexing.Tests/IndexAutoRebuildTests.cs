using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>查詢前自動重建索引，取代手動的 rebuild_index。</summary>
// 改寫行程共用的 RIMWORLD_MOD_MCP_GAME_PATH，不能平行跑。
[NonParallelizable]
public sealed class IndexAutoRebuildTests : IDisposable
{
    private const string SteelDef = """
        <?xml version="1.0" encoding="utf-8"?>
        <Defs>
          <ThingDef>
            <defName>Steel</defName>
          </ThingDef>
        </Defs>
        """;

    private readonly string _root;
    private readonly string? _previousGamePath;
    private readonly CriticalSectionLock _locks;
    private readonly IndexDatabase _database;
    private readonly SourceIndexer _sourceIndexer;
    private readonly IndexAutoRebuild _autoRebuild;

    public IndexAutoRebuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-auto-rebuild-" + Guid.NewGuid().ToString("n")[..12]);
        _previousGamePath = Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH");

        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _locks = new CriticalSectionLock(store, new AlwaysAliveProcessHost());
        var locator = new RimWorldLocator();

        _database = new IndexDatabase(store);
        _sourceIndexer = new SourceIndexer(_database, locator);

        var builder = new IndexBuilder(_database, locator, new IndexFingerprint(), _locks, _sourceIndexer);
        var inspection = new ModInspectionService(_database, new MemberDecompiler(locator), _locks);
        _autoRebuild = new IndexAutoRebuild(locator, builder, _sourceIndexer, inspection);
    }

    public void Dispose()
    {
        _sourceIndexer.Cancel();
        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", _previousGamePath);
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void MissingIndexIsBuiltBeforeTheFirstQuery()
    {
        UseFakeGame();

        _autoRebuild.EnsureReady();

        using var connection = _database.Open();
        Assert.Equal(1L, DefRepository.Count(connection));
        Assert.True(SymbolRepository.Count(connection) > 0);
    }

    /// <summary>檢查要算指紋，太貴不能每次查詢都做：間隔內不再檢查，Invalidate 之後立刻重查。</summary>
    [Test]
    public void ChecksAreThrottledUntilInvalidated()
    {
        UseFakeGame();
        _autoRebuild.EnsureReady();
        _sourceIndexer.Cancel();

        // 讓索引看起來過期：清掉記下的指紋。
        using (var connection = _database.Open())
        {
            IndexMetaRepository.Set(connection, "fingerprint", string.Empty);
        }

        _autoRebuild.EnsureReady();

        using (var connection = _database.Open())
        {
            Assert.Equal(string.Empty, IndexMetaRepository.Get(connection, "fingerprint"));
        }

        _autoRebuild.Invalidate();
        _autoRebuild.EnsureReady();

        using (var connection = _database.Open())
        {
            Assert.False(string.IsNullOrEmpty(IndexMetaRepository.Get(connection, "fingerprint")));
        }
    }

    /// <summary>偵測不到遊戲時重建一定失敗；不能讓查詢因此報錯，照常查現有索引。</summary>
    [Test]
    public void WithoutAGameNothingIsRebuilt()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_root, "no-game")).FullName;
        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", empty);

        _autoRebuild.EnsureReady();

        using var connection = _database.Open();
        Assert.Equal(0L, DefRepository.Count(connection));
        Assert.False(_sourceIndexer.Progress.Running);
    }

    /// <summary>另一個行程正在重建（寫鎖被佔住）時，查詢不能跟著失敗，照舊用現有索引。</summary>
    [Test]
    public void ARebuildBlockedByAnotherWriterDoesNotFailTheQuery()
    {
        UseFakeGame();

        using (_locks.Hold(IndexBuilder.WriteLockName))
        {
            _autoRebuild.EnsureReady();
        }

        using var connection = _database.Open();
        Assert.Equal(0L, DefRepository.Count(connection));
    }

    private void UseFakeGame()
    {
        var install = Path.Combine(_root, "RimWorld");
        var managed = OperatingSystem.IsMacOS() ? Path.Combine(install, "RimWorld.app", "Contents", "MacOS") : install;
        SyntheticAssembly.Emit("namespace Verse { public class Thing { } }", "Assembly-CSharp", managed);
        GameAssemblyManifest.Write(managed, "Assembly-CSharp.dll");

        var defs = Directory.CreateDirectory(Path.Combine(install, "Data", "Core", "Defs")).FullName;
        File.WriteAllText(Path.Combine(defs, "Items.xml"), SteelDef);

        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", install);
    }

    private sealed class AlwaysAliveProcessHost : IProcessHost
    {
        public bool IsAlive(int processId) => true;

        public bool Terminate(int processId, DateTime? expectedStartUtc = null) => true;

        public DateTime? StartTimeUtc(int processId) => DateTime.UtcNow;
    }
}
