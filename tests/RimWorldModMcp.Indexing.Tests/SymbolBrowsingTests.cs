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
