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
    /// <summary>
    /// 遊戲搜尋不能混入 Mod 的原始碼：多版本 Mod 的同一行會重複好幾次，
    /// 而且呼叫端以為自己查的是遊戲本體。Mod 搜尋則一定帶組件樣式。
    /// </summary>
    [Fact]
    public void GameSearchExcludesModAssembliesAndTrimsIndentation()
    {
        var hits = _queries.Search(_connection, "TryStartJob", null, 10);

        Assert.Equal(["Assembly-CSharp"], hits.Select(hit => hit.Assembly).Distinct().ToArray());
        Assert.Equal("public void TryStartJob() { }", hits[0].Text);

        var modHits = _queries.Search(_connection, "TryStartJob", null, 10, assemblyLike: "mod:pkg:%");

        Assert.Equal(["mod:pkg:Extra:abcd"], modHits.Select(hit => hit.Assembly).Distinct().ToArray());
    }

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

    /// <summary>
    /// 群組或 lookaround 內部的 alternation 是「條件」不是「分支」：
    /// <c>Foo(Bar|Baz)?</c>、<c>Foo(?!Bar)</c> 的 Bar 不是必定出現，抽成必要關鍵字
    /// 會把只含 Foo 的檔案在候選階段就濾掉（靜默漏召回）。
    /// 50 個 filler 在目標之前插入（rowid 在前）：全掃 fallback 只看前 limit*4=40 個檔，
    /// 目標排在後面必然看不到；只有「抽到 Foo、走 FTS」能召回。
    /// </summary>
    [Theory]
    [InlineData(@"RenderZone(Quick|Slow)?")]
    [InlineData(@"RenderZone(?!Async)")]
    public void AlternationInsideGroupDoesNotExcludeThePrefix(string pattern)
    {
        for (var i = 0; i < 50; i++)
        {
            SourceFileRepository.Insert(_connection, "Assembly-CSharp", $"Filler/Dum{i:00}.cs",
                $"public class Dum{i:00}\n{{\n    public void Dum{i:00}() {{ }}\n}}\n");
        }

        // 只含 RenderZone、不含 Quick／Slow／Async 的檔案：群組與 lookahead 都是條件，
        // 切分支（Foo(Bar / Baz)?）或抽成必現詞（Bar）都會把它濾掉。
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Only/FooOnly.cs",
            "public class FooOnly\n{\n    public void RenderZone() { }\n}\n");

        var hits = _queries.Search(_connection, pattern, null, 10);

        Assert.Contains(hits, hit => hit.File == "Only/FooOnly.cs");
    }

    /// <summary>
    /// 必要群組內的分支 <c>Foo(Bar|Baz)</c>：候選階段放大到「含 Foo」的所有檔，
    /// regex 自己負責精確比對；把分支切成 <c>Foo(Bar</c> / <c>Baz)</c> 雖然對
    /// <c>FooBar</c> 仍可召回，但關鍵字變成 <c>Foo AND Bar</c>，含 Foo 的其他檔
    /// 會在候選階段消失，等於把 regex 的決定權提前搶走。
    /// </summary>
    [Fact]
    public void AlternationInsideRequiredGroupStillFindsTheTarget()
    {
        for (var i = 0; i < 50; i++)
        {
            SourceFileRepository.Insert(_connection, "Assembly-CSharp", $"Filler/Dum{i:00}.cs",
                $"public class Dum{i:00}\n{{\n    public void Dum{i:00}() {{ }}\n}}\n");
        }

        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Only/ZoneQuick.cs",
            "public class ZoneQuick\n{\n    public void RenderZoneQuick() { }\n}\n");

        // 對照組：regex 只認 FooBar/FooBaz，純 Foo 不該命中，但候選必須包含它（由 FTS 抽出 Foo 提供）。
        SourceFileRepository.Insert(_connection, "Assembly-CSharp", "Only/PlainZone.cs",
            "public class PlainZone\n{\n    public void RenderZone() { }\n}\n");

        var hits = _queries.Search(_connection, @"RenderZone(Quick|Slow)", null, 10);

        Assert.Contains(hits, hit => hit.File == "Only/ZoneQuick.cs");
        Assert.DoesNotContain(hits, hit => hit.File == "Only/PlainZone.cs");
    }

    /// <summary>組件過濾必須在 SQL 層生效，Mod 的搜尋才不會被遊戲本體的命中擠掉。</summary>
    [Fact]
    public void AssemblyFilterRestrictsResults()
    {
        var hits = _queries.Search(_connection, "TryStartJob", null, 10, assemblyLike: "mod:pkg:%");

        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.StartsWith("mod:pkg:", hit.Assembly, StringComparison.Ordinal));
    }

    /// <summary>
    /// 關鍵字抽取曾把 <c>\bFoo\b</c> 抽成 <c>bFoo</c>：反斜線被拿掉、b 黏上去，
    /// FTS 零候選、regex 沒機會跑，最常見的 regex 寫法直接靜默回零筆。
    /// </summary>
    [Fact]
    public void WordBoundaryPatternFindsTheIdentifier()
    {
        var hits = _queries.Search(_connection, @"\bTryStartJob\b", null, 10);

        Assert.Contains(hits, hit => hit.File == "Verse/Thing.cs");
    }

    /// <summary>
    /// regex 本來就會命中識別字的子字串，候選階段也必須找得到（trigram tokenizer）；
    /// unicode61 以整個識別字為 token，Pistol 永遠找不到 Autopistol。
    /// </summary>
    [Fact]
    public void SubstringOfAnIdentifierIsFound()
    {
        var hits = _queries.Search(_connection, "StartJob", null, 10);

        Assert.Contains(hits, hit => hit.File == "Verse/Thing.cs");
    }

    /// <summary>可選片段與字元類裡的字面不是必定出現，抽成必要關鍵字會把正確的檔案濾掉。</summary>
    [Theory]
    [InlineData(@"(Missing)?EndCurrentJob")]
    [InlineData(@"EndCurrentJo[bB]")]
    [InlineData(@"EndCurrentJobs?")]
    [InlineData(@"^\s*public void EndCurrentJob\(\)")]
    public void OptionalPartsDoNotExcludeCandidates(string pattern)
    {
        var hits = _queries.Search(_connection, pattern, null, 10);

        Assert.Contains(hits, hit => hit.File == "RimWorld/Pawn.cs");
    }
}
