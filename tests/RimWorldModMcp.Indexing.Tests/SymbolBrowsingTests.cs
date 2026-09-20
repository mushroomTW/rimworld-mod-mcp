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

    /// <summary>
    /// 大小寫打錯是 LLM 呼叫端最常見的失誤。之前存在性用 LIKE 判（不分大小寫）、
    /// 列子項用 = 查（分大小寫），verse 會通過檢查卻回空清單。
    /// </summary>
    [Fact]
    public void ResolveParentIsCaseInsensitiveAndReturnsTheCanonicalName()
    {
        Assert.Equal("Verse", SymbolRepository.ResolveParent(_connection, "verse", null));
        Assert.Equal("Verse.AI", SymbolRepository.ResolveParent(_connection, "VERSE.ai", null));
        Assert.Equal("Verse.Thing", SymbolRepository.ResolveParent(_connection, "verse.thing", null));
        Assert.Equal("Verse.AI.Group", SymbolRepository.ResolveParent(_connection, "verse.ai.group", null));
        Assert.Equal(string.Empty, SymbolRepository.ResolveParent(_connection, "", null));
        Assert.Null(SymbolRepository.ResolveParent(_connection, "verse.nope", null));

        Assert.NotEmpty(SymbolRepository.Children(_connection, SymbolRepository.ResolveParent(_connection, "verse.thing", null)!, null, null, 10));
    }

    /// <summary>thingdef 該拿到 Verse.ThingDef，而不是落到子字串比對抓一堆 thingDefsToCheck 之類的欄位。</summary>
    [Fact]
    public void ReadFallsBackToCaseInsensitiveExactMatchBeforePartial()
    {
        var hits = SymbolRepository.Read(_connection, "verse.thing", 10);

        Assert.Equal("Verse.Thing", hits[0].Fqn);
        Assert.DoesNotContain(hits, h => h.Fqn == "Verse.Thing.Tick()");

        // 短名也一樣；有同大小寫的候選時排在前面。
        Assert.Equal("Verse.Thing", SymbolRepository.Read(_connection, "thing", 10)[0].Fqn);
        Assert.Equal(0, SymbolRepository.CountPartial(_connection, "verse.thing") - SymbolRepository.CountPartial(_connection, "Verse.Thing"));
    }

    [Fact]
    public void ParentExistsForNamespacesTypesAndNotForUnknown()
    {
        Assert.True(ResolveParent(_connection, "Verse", null) is not null);
        Assert.True(ResolveParent(_connection, "Verse.AI", null) is not null);
        Assert.True(ResolveParent(_connection, "Verse.Thing", null) is not null);
        Assert.True(ResolveParent(_connection, "", null) is not null);
        Assert.False(ResolveParent(_connection, "Verse.Nope", null) is not null);
        Assert.False(ResolveParent(_connection, "Verse", "%mod:pkg%") is not null);
    }

    /// <summary>
    /// 測試專案內的 helper：原本是 <c>SymbolRepository.ParentExists</c>（Middle Man——
    /// 只轉呼叫 <c>ResolveParent</c>，生產程式碼已無呼叫端），移到測試裡避免
    /// 生產 API 為單一測試保留一層沒用的包裝。
    /// </summary>
    private static string? ResolveParent(SqliteConnection connection, string parent, string? assemblyLike)
        => SymbolRepository.ResolveParent(connection, parent, assemblyLike);

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
