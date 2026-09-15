using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Model;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 不靠關鍵字瀏覽符號樹：namespace → 型別與子 namespace，型別 → 成員。
/// </summary>
public sealed class SymbolBrowsingTests : IDisposable
{
    private readonly string _root;
    private readonly IndexDatabase _database;
    private readonly SqliteConnection _connection;

    public SymbolBrowsingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-browse-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(store);
        _connection = _database.Open();

        using var transaction = _connection.BeginTransaction();
        SymbolRepository.Insert(_connection,
        [
            Type("Assembly-CSharp", 1, "Verse.Thing", "Thing", "Verse"),
            Type("Assembly-CSharp", 2, "Verse.AI.JobDriver", "JobDriver", "Verse.AI"),
            Type("Assembly-CSharp", 3, "Verse.AI.Group.Lord", "Lord", "Verse.AI.Group"),
            Type("Assembly-CSharp", 4, "RimWorld.Pawn", "Pawn", "RimWorld"),
            // 巢狀型別：parent 是型別，不能被當成 namespace。
            Type("Assembly-CSharp", 5, "Verse.Thing+Inner", "Inner", "Verse.Thing"),
            Member("Assembly-CSharp", 6, "Verse.Thing.Tick()", "Tick", SymbolKind.Method, "Verse.Thing"),
            Member("Assembly-CSharp", 7, "Verse.Thing.def", "def", SymbolKind.Field, "Verse.Thing"),
            // 與型別同名的成員：精確查詢時型別要排在它前面。
            Member("Assembly-CSharp", 8, "RimWorld.Pawn.Thing", "Thing", SymbolKind.Property, "RimWorld.Pawn"),
            Type("mod:pkg:1.6/Assemblies/X.dll", 1, "Widgets.Widget", "Widget", "Widgets"),
        ]);
        SymbolRepository.RebuildFts(_connection);
        transaction.Commit();
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

    [Fact]
    public void NamespaceListsItsTopLevelTypes()
    {
        var hits = SymbolRepository.Children(_connection, "Verse", null, null, 100);

        Assert.Equal(["Verse.Thing"], hits.Select(h => h.Fqn).ToArray());
    }

    [Fact]
    public void TypeListsMembersAndNestedTypesWithKindFilter()
    {
        var all = SymbolRepository.Children(_connection, "Verse.Thing", null, null, 100);
        var methods = SymbolRepository.Children(_connection, "Verse.Thing", "Method", null, 100);

        Assert.Equal(3, all.Count);
        Assert.Equal(["Verse.Thing.Tick()"], methods.Select(h => h.Fqn).ToArray());
    }

    [Fact]
    public void ChildNamespacesAreOneLevelDeepAndSkipNestedTypeParents()
    {
        Assert.Equal(["Verse.AI"], SymbolRepository.ChildNamespaces(_connection, "Verse", null));
        Assert.Equal(["Verse.AI.Group"], SymbolRepository.ChildNamespaces(_connection, "Verse.AI", null));
        Assert.Equal(["RimWorld", "Verse", "Widgets"], SymbolRepository.ChildNamespaces(_connection, "", null));
    }

    [Fact]
    public void AssemblyFilterAppliesToBrowsing()
    {
        var assemblyLike = "%mod:pkg%";

        Assert.Equal(["Widgets"], SymbolRepository.ChildNamespaces(_connection, "", assemblyLike));
        Assert.Empty(SymbolRepository.Children(_connection, "Verse", null, assemblyLike, 100));
    }

    [Fact]
    public void SuggestSplitsCamelCaseAndMatchesEachToken()
    {
        // 「Verse.ThingWidget」不存在，但拆成 Thing / Widget 後各自能命中。
        var suggestions = SymbolRepository.Suggest(_connection, "Verse.ThingWidget", 5);

        Assert.Contains("Verse.Thing", suggestions);
        Assert.Contains("Widgets.Widget", suggestions);
    }

    [Fact]
    public void SuggestPrefersTypesOverMembersAndIsEmptyWhenNothingResembles()
    {
        var suggestions = SymbolRepository.Suggest(_connection, "Tick", 5);

        // 成員 Verse.Thing.Tick() 也算，但沒有型別命中時仍要回它。
        Assert.Equal(["Verse.Thing.Tick()"], suggestions);
        Assert.Empty(SymbolRepository.Suggest(_connection, "ResolutionUtility", 5));
    }

    /// <summary>
    /// 有精確命中就只回精確命中（型別在前），模糊命中另外計數；
    /// 混著回的話查 ThingDef 會拿到上千筆成員與 ThingDefCount 之類的雜訊。
    /// </summary>
    [Fact]
    public void ReadPrefersExactAndSkipsPartialWhenExactExists()
    {
        var hits = SymbolRepository.Read(_connection, "Thing", 10);

        Assert.Equal(["Verse.Thing", "RimWorld.Pawn.Thing"], hits.Select(h => h.Fqn).ToArray());
        Assert.Equal(3, SymbolRepository.CountPartial(_connection, "Thing"));

        Assert.Equal(["Verse.Thing"], SymbolRepository.Read(_connection, "Verse.Thing", 10).Select(h => h.Fqn).ToArray());
    }

    [Fact]
    public void ReadFallsBackToPartialWhenNoExact()
    {
        var hits = SymbolRepository.Read(_connection, "Thin", 10);

        Assert.Equal(5, hits.Count);
        Assert.All(hits, h => Assert.Contains("Thin", h.Fqn, StringComparison.Ordinal));
    }

    [Fact]
    public void ParentExistsForNamespacesTypesAndNotForUnknown()
    {
        Assert.True(SymbolRepository.ParentExists(_connection, "Verse", null));
        Assert.True(SymbolRepository.ParentExists(_connection, "Verse.AI", null));
        Assert.True(SymbolRepository.ParentExists(_connection, "Verse.Thing", null));
        Assert.True(SymbolRepository.ParentExists(_connection, "", null));
        Assert.False(SymbolRepository.ParentExists(_connection, "Verse.Nope", null));
        Assert.False(SymbolRepository.ParentExists(_connection, "Verse", "%mod:pkg%"));
    }

    private static SymbolRecord Type(string assembly, int token, string fqn, string shortName, string parent) => new()
    {
        Assembly = assembly,
        Fqn = fqn,
        ShortName = shortName,
        Kind = SymbolKind.Class,
        ParentFqn = parent,
        MetadataToken = token,
        Signature = $"public class {fqn}",
        Accessibility = "public",
        IsStatic = false,
    };

    private static SymbolRecord Member(string assembly, int token, string fqn, string shortName, SymbolKind kind, string parent) => new()
    {
        Assembly = assembly,
        Fqn = fqn,
        ShortName = shortName,
        Kind = kind,
        ParentFqn = parent,
        MetadataToken = token,
        Signature = fqn,
        Accessibility = "public",
        IsStatic = false,
    };
}
