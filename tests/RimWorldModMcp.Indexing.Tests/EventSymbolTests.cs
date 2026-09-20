using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 事件從未被索引：存取子被當成 property/event 的一部分略過，事件本身卻沒有另外列，
/// 而 list_symbols 的說明宣稱支援 kind=Event。
/// </summary>
public sealed class EventSymbolTests : IDisposable
{
    private const string Source = """
        using System;

        namespace Verse
        {
            public class TickManager
            {
                public event Action<int> TickChanged;
                public static event EventHandler Paused;
                private event Action hidden;

                public void Raise() { TickChanged?.Invoke(1); Paused?.Invoke(null, EventArgs.Empty); hidden?.Invoke(); }
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-events-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly IReadOnlyList<SymbolRecord> _symbols;

    public EventSymbolTests()
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
    public void EventsAreIndexedWithTypeAccessibilityAndStaticness()
    {
        var events = _symbols.Where(s => s.Kind == SymbolKind.Event).ToDictionary(s => s.ShortName);

        Assert.Equal(["Paused", "TickChanged", "hidden"], events.Keys.Order(StringComparer.Ordinal).ToArray());

        Assert.Equal("Verse.TickManager.TickChanged", events["TickChanged"].Fqn);
        Assert.Equal("Verse.TickManager", events["TickChanged"].ParentFqn);
        Assert.Equal("event System.Action<int> TickChanged", events["TickChanged"].Signature);
        Assert.Equal("public", events["TickChanged"].Accessibility);
        Assert.False(events["TickChanged"].IsStatic);

        Assert.True(events["Paused"].IsStatic);
        Assert.Equal("private", events["hidden"].Accessibility);
    }

    [Fact]
    public void EventAccessorsAreStillNotListedAsMethods()
    {
        Assert.DoesNotContain(_symbols, s => s.Kind == SymbolKind.Method && s.ShortName.StartsWith("add_", StringComparison.Ordinal));
        Assert.DoesNotContain(_symbols, s => s.Kind == SymbolKind.Method && s.ShortName.StartsWith("remove_", StringComparison.Ordinal));
    }
}
