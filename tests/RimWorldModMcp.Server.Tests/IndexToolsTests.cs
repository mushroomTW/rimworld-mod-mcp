using System.Text.Json;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Semantics;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Server.Tests;

public sealed class IndexToolsTests : IAsyncLifetime
{
    private McpServerFixture _server = null!;

    public async Task InitializeAsync()
    {
        _server = await McpServerFixture.StartAsync();
        SeedDatabase();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private void SeedDatabase()
    {
        var database = new IndexDatabase(_server.Store);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        DefRepository.Insert(connection,
        [
            new DefRecord(
                "Core",
                "ThingDef",
                "Apparel_ShieldBelt",
                null,
                "ApparelBase",
                false,
                "shield belt",
                "A projectile-repulsion device.",
                "Core/Defs/ShieldBelt.xml",
                "<ThingDef ParentName=\"ApparelBase\"><defName>Apparel_ShieldBelt</defName></ThingDef>"),
            new DefRecord(
                "Core",
                "ThingDef",
                "Gun_AssaultRifle",
                null,
                null,
                false,
                "assault rifle",
                "A military assault rifle.",
                "Core/Defs/AssaultRifle.xml",
                "<ThingDef><defName>Gun_AssaultRifle</defName></ThingDef>"),
            new DefRecord(
                "Core",
                "ThingDef",
                null,
                "ApparelBase",
                null,
                true,
                "base apparel",
                "Base apparel description.",
                "Core/Defs/ApparelBase.xml",
                "<ThingDef Name=\"ApparelBase\" Abstract=\"True\"></ThingDef>"),
            new DefRecord(
                "Core",
                "ThingDef",
                "Plasteel",
                null,
                null,
                false,
                "plasteel",
                new string('p', 300),
                "Core/Defs/Plasteel.xml",
                "<ThingDef><defName>Plasteel</defName></ThingDef>")
        ]);
        DefRepository.RebuildFts(connection);

        SymbolRepository.Insert(connection,
        [
            new SymbolRecord
            {
                Assembly = "Assembly-CSharp",
                Fqn = "Verse.ThingDef",
                ShortName = "ThingDef",
                Kind = SymbolKind.Class,
                ParentFqn = "Verse",
                MetadataToken = 100,
                Signature = "public class ThingDef : Def",
                BaseChain = "Verse.Def",
                Interfaces = null,
                Accessibility = "Public",
                IsStatic = false,
            },
            new SymbolRecord
            {
                Assembly = "Assembly-CSharp",
                Fqn = "Verse.ShieldBelt",
                ShortName = "ShieldBelt",
                Kind = SymbolKind.Class,
                ParentFqn = "Verse",
                MetadataToken = 101,
                Signature = "public class ShieldBelt : ThingDef",
                BaseChain = "Verse.ThingDef",
                Interfaces = null,
                Accessibility = "Public",
                IsStatic = false,
            },
            new SymbolRecord
            {
                Assembly = "Assembly-CSharp",
                Fqn = "Verse.ThingDef.ResolveReferences()",
                ShortName = "ResolveReferences",
                Kind = SymbolKind.Method,
                ParentFqn = "Verse.ThingDef",
                MetadataToken = 102,
                Signature = "public void ResolveReferences()",
                BaseChain = null,
                Interfaces = null,
                Accessibility = "Public",
                IsStatic = false,
            }
        ]);

        SourceFileRepository.Insert(connection, "Assembly-CSharp", "Verse/ThingDef.cs",
            "using System;\nnamespace Verse;\npublic class ThingDef : Def\n{\n    public string defName;\n    public void ResolveReferences() {}\n}\n");

        // 多版本 Mod 以外，命中橫跨多個組件的情境：驗證各檔案群組自帶 assembly。
        SourceFileRepository.Insert(connection, "Assembly-CSharp", "Verse/Shared.cs", "class A { int sharedToken; }\n");
        SourceFileRepository.Insert(connection, "Assembly-CSharp-firstpass", "Verse/Shared.cs", "class B { int sharedToken; }\n");

        DefReferenceRepository.Insert(connection,
        [
            new DefReference("Gun_AssaultRifle", "Core/Defs/Guns.xml", 7, DefReferenceSource.DefXml, "ThingDef/comps", DefReferenceConfidence.Heuristic),
        ]);

        using (var candidates = new DefReferenceCandidateWriter(connection))
        {
            candidates.Write(new DefReferenceCandidate("Apparel_ShieldBelt", "Core/Defs/Recipe.xml", 45, "xml"));
        }
        DefReferenceRepository.ResolveCandidates(connection);

        transaction.Commit();
        connection.Close();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task RimWorldStatusReturnsDetectionPayload()
    {
        var result = await _server.CallAsync("rimworld_status");
        Assert.True(result.TryGetProperty("detected", out _));
        Assert.True(result.TryGetProperty("index", out var index));
        Assert.True(index.TryGetProperty("fresh", out _));
    }

    [Fact]
    public async Task SearchDefsAndReadDefReturnExpectedResults()
    {
        var searchResult = await _server.CallAsync("search_defs", new Dictionary<string, object?>
        {
            ["query"] = "shield",
            ["limit"] = 10
        });

        Assert.True(searchResult.TryGetProperty("count", out var count) && count.GetInt32() > 0);
        Assert.True(searchResult.TryGetProperty("results", out var results) && results.GetArrayLength() > 0);

        var readResult = await _server.CallAsync("read_def", new Dictionary<string, object?>
        {
            ["def_name"] = "Apparel_ShieldBelt",
            ["def_type"] = "ThingDef"
        });

        Assert.True(readResult.TryGetProperty("count", out var readCount) && readCount.GetInt32() > 0);
    }

    /// <summary>
    /// 預設 encoder 會把 XML 的 &lt; &gt; " 跳脫成 \uXXXX 形式，回傳膨脹三到四成。
    /// 檢查的是 client 實際拿到的 text，不是 structuredContent。
    /// </summary>
    [Fact]
    public async Task ReadDefTextDoesNotUnicodeEscapeXml()
    {
        var result = await _server.Client.CallToolAsync("read_def", new Dictionary<string, object?>
        {
            ["def_name"] = "Apparel_ShieldBelt",
        });

        var text = Assert.Single(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()).Text;

        Assert.Contains("<defName>Apparel_ShieldBelt</defName>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u003C", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadSymbolAndSuggestions()
    {
        var readResult = await _server.CallAsync("read_symbol", new Dictionary<string, object?>
        {
            ["name"] = "Verse.ThingDef",
            ["include_body"] = false
        });

        Assert.True(readResult.TryGetProperty("count", out var count) && count.GetInt32() == 1);

        var missResult = await _server.CallAsync("read_symbol", new Dictionary<string, object?>
        {
            ["name"] = "ThingDefZzz",
            ["include_body"] = false
        });

        Assert.True(missResult.TryGetProperty("count", out var missCount) && missCount.GetInt32() == 0);
        Assert.True(missResult.TryGetProperty("suggestions", out _));
    }

    [Fact]
    public async Task ListSymbolsAndFindDescendants()
    {
        var listResult = await _server.CallAsync("list_symbols", new Dictionary<string, object?>
        {
            ["parent"] = "Verse",
            ["limit"] = 10
        });

        Assert.True(listResult.TryGetProperty("count", out var count) && count.GetInt32() > 0);

        var listMembers = await _server.CallAsync("list_symbols", new Dictionary<string, object?>
        {
            ["parent"] = "Verse.ThingDef",
            ["limit"] = 10
        });

        Assert.True(listMembers.TryGetProperty("count", out _));

        var descendants = await _server.CallAsync("find_descendants", new Dictionary<string, object?>
        {
            ["base_type"] = "Verse.ThingDef",
            ["limit"] = 10
        });

        Assert.True(descendants.TryGetProperty("count", out var dCount) && dCount.GetInt32() >= 1);
    }

    /// <summary>
    /// 型別的 signature 已含 kind 與完整名稱，name／kind 再列一次只是重複付 token；
    /// 成員的 kind 從 signature 的形狀（括號、get/set、event）就看得出來。
    /// </summary>
    [Fact]
    public async Task SymbolBriefsCarryNoFieldsRedundantWithSignature()
    {
        var descendants = await _server.CallAsync("find_descendants", new Dictionary<string, object?>
        {
            ["base_type"] = "Verse.ThingDef",
        });

        var type = Assert.Single(descendants.GetProperty("results").EnumerateArray());
        Assert.Equal("public class ShieldBelt : ThingDef", type.GetProperty("signature").GetString());
        Assert.False(type.TryGetProperty("name", out _));
        Assert.False(type.TryGetProperty("kind", out _));

        var members = await _server.CallAsync("list_symbols", new Dictionary<string, object?>
        {
            ["parent"] = "Verse.ThingDef",
        });

        var member = Assert.Single(members.GetProperty("results").EnumerateArray());
        Assert.Equal("ResolveReferences()", member.GetProperty("name").GetString());
        Assert.False(member.TryGetProperty("kind", out _));
    }

    /// <summary>組件全部相同時提到結果層，命中依檔案分組：每列重複 assembly 與 file 是最大宗的浪費。</summary>
    [Fact]
    public async Task SearchSourceGroupsHitsByFile()
    {
        var result = await _server.CallAsync("search_source", new Dictionary<string, object?>
        {
            ["pattern"] = "defName|ResolveReferences",
        });

        Assert.Equal(2, result.GetProperty("count").GetInt32());
        Assert.Equal("Assembly-CSharp", result.GetProperty("assembly").GetString());

        var file = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal("Verse/ThingDef.cs", file.GetProperty("file").GetString());
        Assert.False(file.TryGetProperty("assembly", out _));
        Assert.Equal([5, 6], file.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("line").GetInt32()));
    }

    /// <summary>依檔案分組；confidence 只在不是 exact 時出現。</summary>
    [Fact]
    public async Task FindDefUsagesGroupsHitsByFile()
    {
        var result = await _server.CallAsync("find_def_usages", new Dictionary<string, object?>
        {
            ["def_name"] = "Apparel_ShieldBelt",
        });

        var file = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal("Core/Defs/Recipe.xml", file.GetProperty("file_path").GetString());
        Assert.Equal("def_xml", file.GetProperty("source_kind").GetString());

        var hit = Assert.Single(file.GetProperty("hits").EnumerateArray());
        Assert.Equal(45, hit.GetProperty("line").GetInt32());
        Assert.False(hit.TryGetProperty("confidence", out _));
    }

    /// <summary>命中橫跨多個組件時結果層 assembly 為 null，各檔案群組必須自帶，否則無法接 read_source_file。</summary>
    [Fact]
    public async Task SearchSourceGroupsCarryAssemblyWhenHitsSpanAssemblies()
    {
        var result = await _server.CallAsync("search_source", new Dictionary<string, object?>
        {
            ["pattern"] = "sharedToken",
        });

        Assert.True(!result.TryGetProperty("assembly", out var shared) || shared.ValueKind == JsonValueKind.Null);
        Assert.Equal(
            ["Assembly-CSharp", "Assembly-CSharp-firstpass"],
            result.GetProperty("results").EnumerateArray().Select(f => f.GetProperty("assembly").GetString()).Order());
    }

    [Fact]
    public async Task FindDefUsagesKeepsHeuristicConfidence()
    {
        var result = await _server.CallAsync("find_def_usages", new Dictionary<string, object?>
        {
            ["def_name"] = "Gun_AssaultRifle",
        });

        var file = Assert.Single(result.GetProperty("results").EnumerateArray());
        var hit = Assert.Single(file.GetProperty("hits").EnumerateArray());
        Assert.Equal("heuristic", hit.GetProperty("confidence").GetString());
    }

    [Fact]
    public async Task SearchDefsTruncatesDescriptionTo80Chars()
    {
        var result = await _server.CallAsync("search_defs", new Dictionary<string, object?>
        {
            ["query"] = "Plasteel",
        });

        var def = Assert.Single(result.GetProperty("results").EnumerateArray());
        Assert.Equal(new string('p', 80) + "…", def.GetProperty("description").GetString());
    }

    [Fact]
    public async Task SearchSourceAndReadSourceFile()
    {
        var searchResult = await _server.CallAsync("search_source", new Dictionary<string, object?>
        {
            ["pattern"] = "ResolveReferences",
            ["limit"] = 10
        });

        Assert.True(searchResult.TryGetProperty("count", out var count) && count.GetInt32() == 1);

        var readResult = await _server.CallAsync("read_source_file", new Dictionary<string, object?>
        {
            ["assembly"] = "Assembly-CSharp",
            ["file"] = "Verse/ThingDef.cs"
        });

        Assert.True(readResult.TryGetProperty("text", out var text) && !string.IsNullOrEmpty(text.GetString()));

        var pagedResult = await _server.CallAsync("read_source_file", new Dictionary<string, object?>
        {
            ["assembly"] = "Assembly-CSharp",
            ["file"] = "Verse/ThingDef.cs",
            ["start_line"] = 2
        });

        Assert.True(pagedResult.TryGetProperty("text", out _));
    }

    [Fact]
    public async Task FindDefUsagesReturnsResolvedUsages()
    {
        var usagesResult = await _server.CallAsync("find_def_usages", new Dictionary<string, object?>
        {
            ["def_name"] = "Apparel_ShieldBelt",
            ["limit"] = 10
        });

        Assert.True(usagesResult.TryGetProperty("count", out var count) && count.GetInt32() == 1);
    }

    [Fact]
    public async Task RebuildIndexTriggerAsync()
    {
        var result = await _server.CallAsync("rebuild_index", new Dictionary<string, object?>
        {
            ["index_source"] = false,
            ["only_source"] = true
        });

        Assert.True(result.TryGetProperty("def_count", out _));
    }
}
