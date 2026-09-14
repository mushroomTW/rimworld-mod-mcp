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

    /// <summary>
    /// alternation 的候選必須用 FTS 的 OR 取聯集：退回全掃時只看前 limit*4 個檔，
    /// 目標排在大量檔案後面就會靜默漏掉（實戰中 Framework 級體量必中）。
    /// 目標刻意在 50 個 filler 之後插入（rowid 在後），舊 fallback（前 40 個檔）
    /// 必然看不到它們；只有 OR 聯集能召回。
    /// </summary>
    [Fact]
    public void AlternationRecallsTargetsBeyondTheFallbackWindow()
    {
        for (var i = 0; i < 50; i++)
        {
            SourceFileRepository.Insert(_connection, "Assembly-CSharp", $"Filler/Noise{i:00}.cs",
                $"public class Noise{i:00}\n{{\n    public void Fill{i:00}() {{ }}\n}}\n");
        }

        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Late/Alpha.cs",
            "public class Alpha\n{\n    public void TryStartJob() { }\n}\n");
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Late/Beta.cs",
            "public class Beta\n{\n    public void EndCurrentJob() { }\n}\n");

        var hits = _queries.Search(_connection, "TryStartJob|EndCurrentJob", null, 10);

        var files = hits.Select(hit => hit.File).Distinct().ToList();

        Assert.Contains("Late/Alpha.cs", files);
        Assert.Contains("Late/Beta.cs", files);
    }

    /// <summary>
    /// 分組寫法 <c>(a|b)</c> 的關鍵字也要能抽出，不能退回全掃。
    /// 50 個 filler 保證全掃的前 40 個檔看不到目標。
    /// </summary>
    [Fact]
    public void GroupedAlternationUsesTheFtsIndex()
    {
        for (var i = 0; i < 50; i++)
        {
            SourceFileRepository.Insert(_connection, "Assembly-CSharp", $"Filler/G{i:00}.cs",
                $"public class G{i:00}\n{{\n    public void Fill{i:00}() {{ }}\n}}\n");
        }

        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Late/Gamma.cs",
            "public class Gamma\n{\n    public void DeregisterZone() { }\n}\n");

        var hits = _queries.Search(_connection, "(DeregisterZone|Delete)", null, 10);

        Assert.Contains(hits, hit => hit.File == "Late/Gamma.cs");
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
