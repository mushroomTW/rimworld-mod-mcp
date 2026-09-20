using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 第三層索引的續跑：行程重啟殺掉背景工作後，做完的組件不該重來。
/// 用兩顆合成的 Assembly-CSharp* 組件模擬遊戲的 Managed 目錄。
/// </summary>
// 兩個類別都改寫行程共用的 RIMWORLD_MOD_MCP_GAME_PATH，不能平行跑。
[Collection("GamePathEnvironment")]
public sealed class SourceIndexerTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousGamePath;
    private readonly IndexDatabase _database;
    private readonly SourceIndexer _indexer;

    public SourceIndexerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-source-indexer-" + Guid.NewGuid().ToString("n")[..12]);
        _previousGamePath = Environment.GetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH");

        var install = Path.Combine(_root, "RimWorld");
        var managed = Path.Combine(install, ManagedRelativePath());
        Directory.CreateDirectory(Path.Combine(install, "Data", "Core", "Defs"));

        SyntheticAssembly.Emit("namespace Verse { public class Thing { public void Tick() { } } }", "Assembly-CSharp", managed);
        SyntheticAssembly.Emit("namespace LudeonTK { public class Early { public void Boot() { } } }", "Assembly-CSharp-firstpass", managed);

        Environment.SetEnvironmentVariable("RIMWORLD_MOD_MCP_GAME_PATH", install);

        _database = new IndexDatabase(new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache")));
        _indexer = new SourceIndexer(_database, new RimWorldLocator());
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

    private static string ManagedRelativePath() => OperatingSystem.IsWindows()
        ? Path.Combine("RimWorldWin64_Data", "Managed")
        : OperatingSystem.IsMacOS()
            ? Path.Combine("RimWorldMac.app", "Contents", "Resources", "Data", "Managed")
            : Path.Combine("RimWorldLinux_Data", "Managed");

    [Fact]
    public void RunIndexesEveryGameAssemblyAndMarksThemDone()
    {
        var progress = _indexer.Run();

        Assert.True(progress.Completed, progress.Error);
        Assert.Equal(2, progress.FilesIndexed);

        using var connection = _database.Open();
        Assert.Equal("true", IndexMetaRepository.Get(connection, "source_indexed"));
        Assert.NotNull(IndexMetaRepository.Get(connection, SourceIndexer.DoneKeyPrefix + "Assembly-CSharp"));
        Assert.NotNull(IndexMetaRepository.Get(connection, SourceIndexer.DoneKeyPrefix + "Assembly-CSharp-firstpass"));
        Assert.False(_indexer.IsInterrupted());
    }

    /// <summary>
    /// 模擬被中斷：firstpass 已做完（有 done 標記），Assembly-CSharp 沒有。
    /// 續跑必須跳過 firstpass（它的內容原封不動）、只重做 Assembly-CSharp。
    /// </summary>
    [Fact]
    public void ResumeSkipsAssembliesThatWereAlreadyFinished()
    {
        _indexer.Run();

        using (var connection = _database.Open())
        {
            // 把 firstpass 的內容改成一個「哨兵」：若續跑重做了它，哨兵就會被覆蓋。
            SourceFileRepository.Insert(connection, "Assembly-CSharp-firstpass", "LudeonTK/Early.cs", "// sentinel");

            // 第一層索引存在（rebuild_index 跑過）是續跑的前提之一。
            IndexMetaRepository.Set(connection, "fingerprint", "abc");

            using var interrupt = connection.CreateCommand();
            interrupt.CommandText = """
                UPDATE index_meta SET value = 'false' WHERE key = 'source_indexed';
                DELETE FROM index_meta WHERE key = $done;
                DELETE FROM source_file WHERE assembly = 'Assembly-CSharp';
                """;
            interrupt.Parameters.AddWithValue("$done", SourceIndexer.DoneKeyPrefix + "Assembly-CSharp");
            interrupt.ExecuteNonQuery();
        }

        Assert.True(_indexer.IsInterrupted());

        var progress = _indexer.Run();

        Assert.True(progress.Completed, progress.Error);
        Assert.Equal(2, progress.FilesIndexed);

        using var check = _database.Open();
        Assert.Equal("// sentinel", SourceFileRepository.Read(check, "Assembly-CSharp-firstpass", "LudeonTK/Early.cs"));
        Assert.Contains("class Thing", SourceFileRepository.Read(check, "Assembly-CSharp", "Verse/Thing.cs"));
        Assert.False(_indexer.IsInterrupted());
    }

    /// <summary>rebuild_index 清空 source_file 時，done 標記必須一起消失，否則續跑會跳過空掉的組件。</summary>
    [Fact]
    public void ClearingSourceFilesForgetsTheDoneMarkers()
    {
        _indexer.Run();

        using var connection = _database.Open();
        SourceFileRepository.Clear(connection);

        Assert.Null(IndexMetaRepository.Get(connection, SourceIndexer.DoneKeyPrefix + "Assembly-CSharp"));
        Assert.Null(IndexMetaRepository.Get(connection, SourceIndexer.DoneKeyPrefix + "Assembly-CSharp-firstpass"));
    }

    [Fact]
    public void FreshDatabaseIsNotConsideredInterrupted()
    {
        Assert.False(_indexer.IsInterrupted());
    }
}
