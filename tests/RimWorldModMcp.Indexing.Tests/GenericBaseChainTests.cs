using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 泛型基底鏈的解析。
///
/// <para>
/// <c>class Foo : Comp&lt;Bar&gt;</c> 的基底 handle 是 TypeSpecification 而非
/// TypeDefinition——不解開它，繼承鏈在第一層就斷，RimWorld 大量以泛型為
/// 基底的家族在 find_descendants 全部隱形。
/// </para>
/// </summary>
public sealed class GenericBaseChainTests : IDisposable
{
    private const string Source = """
        namespace TestNs
        {
            public class Comp<T> { }

            public class Bar { }

            public class Foo : Comp<Bar> { }

            public class Widget
            {
                public void Configure(out int result, ref int seed, int plain)
                {
                    result = seed + plain;
                }
            }
        }
        """;

    private readonly string _root;
    private readonly IReadOnlyList<Model.SymbolRecord> _symbols;

    public GenericBaseChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-generic-" + Guid.NewGuid().ToString("n")[..12]);

        var assembly = SyntheticAssembly.Emit(Source, "GenericChain", _root);
        _symbols = new AssemblySymbolReader().Read(assembly);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void GenericBaseAppearsInTheChainWithoutInstantiationArguments()
    {
        var foo = _symbols.Single(s => s.Fqn == "TestNs.Foo");

        Assert.NotNull(foo.BaseChain);

        var chain = foo.BaseChain!.Split('|');

        // 去除引數的定義名稱，且鏈要繼續走到 System.Object 而不是斷在第一層。
        Assert.Contains("TestNs.Comp", chain);
        Assert.Contains("System.Object", chain);
    }

    [Fact]
    public void DescendantsFindsSubclassesOfGenericBases()
    {
        var storeRoot = Path.Combine(_root, "db");
        var store = new StoreDirectories(Path.Combine(storeRoot, "data"), Path.Combine(storeRoot, "cache"));
        var database = new IndexDatabase(store);

        try
        {
            using var connection = database.Open();

            SymbolRepository.Insert(connection, _symbols);
            SymbolRepository.RebuildFts(connection);

            // 泛型引數不論寫不寫都要查得到。
            Assert.Contains(SymbolRepository.Descendants(connection, "TestNs.Comp", 10), hit => hit.Fqn == "TestNs.Foo");
            Assert.Contains(SymbolRepository.Descendants(connection, "TestNs.Comp<T>", 10), hit => hit.Fqn == "TestNs.Foo");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// out 與 ref 必須能區分——Harmony patch 的 prefix 要照著簽章寫，
    /// 兩者混淆會讓產生的 patch 無法編譯。
    /// </summary>
    [Fact]
    public void OutAndRefParametersAreDistinguished()
    {
        var method = _symbols.Single(s => s.Fqn == "TestNs.Widget.Configure");

        Assert.Contains("out int result", method.Signature, StringComparison.Ordinal);
        Assert.Contains("ref int seed", method.Signature, StringComparison.Ordinal);
        Assert.Contains("int plain", method.Signature, StringComparison.Ordinal);
    }
}
