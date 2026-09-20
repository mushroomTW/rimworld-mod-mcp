using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 泛型外層底下的巢狀型別。IL 會讓巢狀型別重新宣告外層的泛型參數，
/// 一不小心 <c>Verse.Dijkstra`1+DistanceComparer</c> 就會被組成 <c>Verse.Dijkstra&lt;T&gt;</c>——
/// 巢狀名稱消失、與外層撞同一個 fqn、parent 又是沒人查得到的原始 <c>Verse.Dijkstra`1</c>。
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
}
