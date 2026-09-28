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
                "<ThingDef Name=\"ApparelBase\" Abstract=\"True\"></ThingDef>")
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
