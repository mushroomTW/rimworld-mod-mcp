using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 多版本 Mod 的組件鍵與索引生命週期。
///
/// <para>
/// 典型的 Workshop Mod 在 <c>1.5/Assemblies</c> 與 <c>1.6/Assemblies</c> 各放一份同名 DLL，
/// 組件鍵必須讓人看得出是哪一份，而且要能拿版本片段當篩選條件。
/// </para>
/// </summary>
public sealed class ModInspectionServiceTests : IDisposable
{
    private const string Source = """
        namespace Widgets
        {
            public class Widget
            {
                public void TryStartJob() { }
            }
        }
        """;

    private readonly string _root;
    private readonly string _modPath;
    private readonly IndexDatabase _database;
    private readonly MemberDecompiler _decompiler = new();
    private readonly ModInspectionService _service;

    public ModInspectionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-inspect-" + Guid.NewGuid().ToString("n")[..12]);
        _modPath = Path.Combine(_root, "mod");
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _database = new IndexDatabase(store);
        _service = new ModInspectionService(
            _database, _decompiler, new CriticalSectionLock(store, new AlwaysAliveProcessHost()), new SourceQueryService());

        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "1.5", "Assemblies"));
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "1.6", "Assemblies"));
    }

    public void Dispose()
    {
        _decompiler.Dispose();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AssemblyKeyIsTheRelativePathInsideTheMod()
    {
        var assemblies = _service.Inspect("pkg", _modPath);

        Assert.Equal(
            ["mod:pkg:1.5/Assemblies/Widget.dll", "mod:pkg:1.6/Assemblies/Widget.dll"],
            assemblies.Select(a => a.Key).Order().ToArray());
        Assert.All(assemblies, a => Assert.True(a.SymbolCount > 0));
    }

    /// <summary>同一個類別在兩份 DLL 各有一筆；帶版本片段篩選後只剩一筆。</summary>
    [Fact]
    public void AssemblyFilterPicksOneVersion()
    {
        _service.Inspect("pkg", _modPath);

        using var connection = _database.Open();
        var all = SymbolRepository.Read(connection, "Widget", 10);
        var only16 = SymbolRepository.Read(connection, "Widget", 10, assemblyLike: ModInspectionService.AssemblyLike("pkg", "1.6/"));

        Assert.Equal(2, all.Count(h => h.Kind == "Class"));
        Assert.Single(only16, h => h.Kind == "Class");
        Assert.Equal("mod:pkg:1.6/Assemblies/Widget.dll", only16[0].Assembly);
    }

    [Fact]
    public void SearchSourceHonoursTheAssemblyFilter()
    {
        var (hits, indexed) = _service.SearchSource("pkg", _modPath, "TryStartJob", 10, assemblyFilter: "1.5/");

        Assert.True(indexed);
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("mod:pkg:1.5/Assemblies/Widget.dll", h.Assembly));
    }

    /// <summary>Mod 更新後移除的版本目錄，下一次檢視就要把它的索引清掉。</summary>
    [Fact]
    public void RemovedAssemblyIsForgottenOnNextInspect()
    {
        _service.Inspect("pkg", _modPath);
        Directory.Delete(Path.Combine(_modPath, "1.5"), recursive: true);

        var assemblies = _service.Inspect("pkg", _modPath);

        Assert.Single(assemblies);
        Assert.Equal("mod:pkg:1.6/Assemblies/Widget.dll", assemblies[0].Key);

        using var connection = _database.Open();
        Assert.Empty(SymbolRepository.Read(connection, "Widget", 10, assemblyLike: ModInspectionService.AssemblyLike("pkg", "1.5/")));
        Assert.Null(SourceFileRepository.Read(connection, "mod:pkg:1.5/Assemblies/Widget.dll", "Widgets/Widget.cs"));
        Assert.Null(IndexMetaRepository.Get(connection, "mod_stamp:mod:pkg:1.5/Assemblies/Widget.dll"));
    }

    private sealed class AlwaysAliveProcessHost : IProcessHost
    {
        public bool IsAlive(int processId) => true;

        public bool Terminate(int processId, DateTime? expectedStartUtc = null) => true;

        public DateTime? StartTimeUtc(int processId) => null;
    }
}
