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
    private readonly MemberDecompiler _decompiler = new(new RimWorldLocator());
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

    /// <summary>
    /// <c>Source/</c> 底下的 bin／obj 是使用者自己的建置產物（常含測試組件），
    /// 遊戲不會載入它們，索引只是雜訊，還會讓 dotnet build 撞上被佔用的 DLL。
    /// </summary>
    [Fact]
    public void BuildOutputsUnderSourceAreNotTreatedAsModAssemblies()
    {
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "Source", "Widget.Tests", "bin", "Debug", "net472"));
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "Source", "Widget", "obj", "Debug", "net472"));
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "Source", "Libs"));

        var assemblies = _service.Inspect("pkg", _modPath);

        Assert.Equal(
            ["mod:pkg:1.5/Assemblies/Widget.dll", "mod:pkg:1.6/Assemblies/Widget.dll"],
            assemblies.Select(a => a.Key).Order().ToArray());
    }

    /// <summary>
    /// 遊戲只從 Assemblies/ 載入。實機上 Mod 內的 LastVersion/1.5/X.dll 這類備份被索引後，
    /// 同一行在搜尋結果裡重複出現三次，符號數也跟著灌水。
    /// </summary>
    [Fact]
    public void DllsOutsideAnAssembliesFolderAreIgnored()
    {
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "LastVersion", "1.5"));
        SyntheticAssembly.Emit(Source, "Widget", Path.Combine(_modPath, "Libs"));

        var assemblies = _service.Inspect("pkg", _modPath);

        Assert.Equal(
            ["mod:pkg:1.5/Assemblies/Widget.dll", "mod:pkg:1.6/Assemblies/Widget.dll"],
            assemblies.Select(a => a.Key).Order().ToArray());
    }

    [Fact]
    public void LockNameIsPerPackageAndFilesystemSafe()
    {
        var a = ModInspectionService.LockName("brrainz.framework");
        var b = ModInspectionService.LockName("other.Mod");

        Assert.NotEqual(a, b);
        Assert.StartsWith("mod-index-", a, StringComparison.Ordinal);
        Assert.DoesNotContain("/", a);
        Assert.DoesNotContain("\\", a);
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
        // 先同步索引好，這裡只驗證篩選。
        _service.Inspect("pkg", _modPath);

        var result = _service.TrySearchSource("pkg", _modPath, "TryStartJob", 10, assemblyFilter: "1.5/");

        Assert.True(result.SourceIndexed);
        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Equal("mod:pkg:1.5/Assemblies/Widget.dll", h.Assembly));
    }

    /// <summary>
    /// rebuild_index 會把 symbol 與 source_file 整表清掉（不分遊戲與 Mod）。
    /// 之後 Mod 的 stamp 若還在，Inspect 會誤判已快取而回傳零符號、search_source 也回 source_indexed=true 卻零命中。
    /// </summary>
    [Fact]
    public void RebuildClearsModIndexSoNextInspectReindexes()
    {
        _service.Inspect("pkg", _modPath);

        using (var connection = _database.Open())
        {
            IndexBuilder.ClearAll(connection);
        }

        var assemblies = _service.Inspect("pkg", _modPath);

        Assert.All(assemblies, a => Assert.False(a.FromCache));
        Assert.All(assemblies, a => Assert.True(a.SymbolCount > 0));
        Assert.NotEmpty(_service.TrySearchSource("pkg", _modPath, "TryStartJob", 10).Hits);
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

    /// <summary>
    /// 第一次搜尋不同步反編譯：立刻回「索引中」，背景完成後再搜就有結果。
    /// 大 Mod 同步反編譯要幾分鐘，MCP client 的呼叫逾時通常只有一兩分鐘。
    /// </summary>
    [Fact]
    public async Task FirstSearchStartsIndexingInBackgroundAndLaterSearchesHit()
    {
        var first = _service.TrySearchSource("pkg", _modPath, "TryStartJob", 10);

        Assert.True(first.Indexing);
        Assert.False(first.SourceIndexed);
        Assert.Empty(first.Hits);

        // 索引中再呼叫一次不會再起一個背景工作，狀態仍是索引中或已完成。
        var second = _service.TrySearchSource("pkg", _modPath, "TryStartJob", 10);
        Assert.True(second.Indexing || second.SourceIndexed);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        while (_service.TrySearchSource("pkg", _modPath, "TryStartJob", 10) is { Indexing: true })
        {
            await Task.Delay(50, cancellation.Token);
        }

        var done = _service.TrySearchSource("pkg", _modPath, "TryStartJob", 10);

        Assert.True(done.SourceIndexed);
        Assert.Null(done.Error);
        Assert.Equal(2, done.Hits.Count);
    }

    [Fact]
    public void SearchOnXmlOnlyModReportsNotIndexedWithoutIndexing()
    {
        var xmlOnly = Path.Combine(_root, "xml-only");
        Directory.CreateDirectory(Path.Combine(xmlOnly, "Defs"));

        var result = _service.TrySearchSource("xml", xmlOnly, "anything", 10);

        Assert.False(result.Indexing);
        Assert.False(result.SourceIndexed);
        Assert.Empty(result.Hits);
    }

    private sealed class AlwaysAliveProcessHost : IProcessHost
    {
        public bool IsAlive(int processId) => true;

        public bool Terminate(int processId, DateTime? expectedStartUtc = null) => true;

        public DateTime? StartTimeUtc(int processId) => null;
    }
}
