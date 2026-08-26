using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

public sealed class SourceQueryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly IndexDatabase _database;
    private readonly SqliteConnection _connection;
    private readonly SourceQueryService _queries = new();

    public SourceQueryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-query-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(store);
        _connection = _database.Open();

        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Verse/Thing.cs",
            "public class Thing\n{\n    public void TryStartJob() { }\n}\n");
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "RimWorld/Pawn.cs",
            "public class Pawn\n{\n    public void EndCurrentJob() { }\n}\n");
        SourceFileRepository.Insert(_connection, "mod:pkg:Extra:abcd", "Extra/Widget.cs",
            "public class Widget\n{\n    public void TryStartJob() { }\n}\n");
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// file_pattern 的 glob 轉換曾經完全失效（先跳脫再替換找不到目標序列），
    /// 任何帶樣式的搜尋都靜默回傳零筆。
    /// </summary>
    [Fact]
    public void FilePatternNarrowsResults()
    {
        var hits = _queries.Search(_connection, "class", "Verse/*", 10);

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.StartsWith("Verse/", hit.File, StringComparison.Ordinal));
    }

    /// <summary>
    /// regex 的 alternation 不能被翻成 FTS 的隱含 AND——那會把只含其中
    /// 一支的檔案在候選階段就濾掉，regex 根本沒機會執行。
    /// </summary>
    [Fact]
    public void AlternationFindsFilesContainingEitherBranch()
    {
        var hits = _queries.Search(_connection, "TryStartJob|EndCurrentJob", null, 10);

        var files = hits.Select(hit => hit.File).Distinct().ToList();

        Assert.Contains("Verse/Thing.cs", files);
        Assert.Contains("RimWorld/Pawn.cs", files);
    }

    /// <summary>組件過濾必須在 SQL 層生效，Mod 的搜尋才不會被遊戲本體的命中擠掉。</summary>
    [Fact]
    public void AssemblyFilterRestrictsResults()
    {
        var hits = _queries.Search(_connection, "TryStartJob", null, 10, assemblyLike: "mod:pkg:%");

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.StartsWith("mod:pkg:", hit.Assembly, StringComparison.Ordinal));
    }
}
