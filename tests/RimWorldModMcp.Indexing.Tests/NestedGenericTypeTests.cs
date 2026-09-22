using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 泛型外層底下的巢狀型別。IL 會讓巢狀型別重新宣告外層的泛型參數，
/// 一不小心 Verse.Dijkstra`1+DistanceComparer 就會被組成 Verse.Dijkstra＜T＞——
/// 巢狀名稱消失、與外層撞同一個 fqn、parent 又是沒人查得到的原始 Verse.Dijkstra`1。
/// 遊戲本體裡就有四個這樣的型別。
/// </summary>
public sealed class NestedGenericTypeTests : IDisposable
{
    private const string Source = """
        namespace Verse
        {
            public class Dijkstra<T>
            {
                public class DistanceComparer
                {
                    public DistanceComparer() { }
                    public int Compare(T a, T b) => 0;
                }

                public struct Node<TValue>
                {
                    public TValue value;
                }
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-nested-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly IReadOnlyList<SymbolRecord> _symbols;

    public NestedGenericTypeTests()
    {
        _symbols = new AssemblySymbolReader().Read(SyntheticAssembly.Emit(Source, "Assembly-CSharp", _root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void NestedTypeKeepsItsOwnNameAndOnlyItsOwnParameters()
    {
        var comparer = _symbols.Single(s => s.Kind == SymbolKind.Class && s.ShortName == "DistanceComparer");
        var node = _symbols.Single(s => s.Kind == SymbolKind.Struct && s.ShortName == "Node<TValue>");

        Assert.Equal("Verse.Dijkstra<T>+DistanceComparer", comparer.Fqn);
        Assert.Equal("Verse.Dijkstra<T>+Node<TValue>", node.Fqn);
    }

    [Fact]
    public void NestedTypeParentIsTheOuterTypeDisplayName()
    {
        var outer = _symbols.Single(s => s.Kind == SymbolKind.Class && s.ShortName == "Dijkstra<T>");
        var comparer = _symbols.Single(s => s.Kind == SymbolKind.Class && s.ShortName == "DistanceComparer");

        Assert.Equal("Verse.Dijkstra<T>", outer.Fqn);
        Assert.Equal(outer.Fqn, comparer.ParentFqn);

        // 外層與巢狀型別絕不能共用同一個 fqn。
        Assert.Single(_symbols, s => s.Fqn == "Verse.Dijkstra<T>" && s.Kind != SymbolKind.Constructor);
    }

    [Fact]
    public void NestedConstructorIsNamedAfterTheNestedType()
    {
        var constructor = _symbols.Single(s => s.Kind == SymbolKind.Constructor && s.ParentFqn == "Verse.Dijkstra<T>+DistanceComparer");

        Assert.Equal("DistanceComparer", constructor.ShortName);
        Assert.Equal("Verse.Dijkstra<T>+DistanceComparer.DistanceComparer", constructor.Fqn);
    }

    /// <summary>
    /// F10：泛型實例化為參數時，巢狀層不可被截掉。
    /// `Take(Outer&lt;int&gt;.Nested)` 舊實作顯示為 `Take(Outer&lt;int&gt;)`。
    /// </summary>
    [Fact]
    public void NestedGenericInstantiationInParameterKeepsTheNestedLevel()
    {
        const string source = """
            namespace Verse
            {
                public class Outer<T>
                {
                    public class Nested { }
                    public void Take(Outer<int>.Nested value) { }
                }
            }
            """;

        var root = Path.Combine(Path.GetTempPath(), "rwmm-nested-param-" + Guid.NewGuid().ToString("n")[..12]);

        try
        {
            var symbols = new AssemblySymbolReader().Read(SyntheticAssembly.Emit(source, "Assembly-CSharp", root));
            var method = symbols.Single(s => s.Kind == SymbolKind.Method && s.ShortName == "Take");

            Assert.Contains("Outer<int>+Nested", method.Signature, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// F10：巢狀泛型為基底時，基底鏈不可混淆為外層型別。
    /// `Derived : Outer&lt;int&gt;.Nested` 舊鏈為 `Outer|Object`。
    /// </summary>
    [Fact]
    public void NestedGenericBaseChainKeepsTheNestedLevel()
    {
        const string source = """
            namespace Verse
            {
                public class Outer<T>
                {
                    public class Nested { }
                }
                public class Derived : Outer<int>.Nested { }
            }
            """;

        var root = Path.Combine(Path.GetTempPath(), "rwmm-nested-base-" + Guid.NewGuid().ToString("n")[..12]);

        try
        {
            var symbols = new AssemblySymbolReader().Read(SyntheticAssembly.Emit(source, "Assembly-CSharp", root));
            var derived = symbols.Single(s => s.Kind == SymbolKind.Class && s.ShortName == "Derived");

            Assert.NotNull(derived.BaseChain);
            Assert.Contains("Verse.Outer+Nested", derived.BaseChain!, StringComparison.Ordinal);
            Assert.DoesNotContain("Verse.Outer|", derived.BaseChain!, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
