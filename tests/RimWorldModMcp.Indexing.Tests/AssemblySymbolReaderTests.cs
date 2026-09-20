using RimWorldModMcp.Indexing.Metadata;
using RimWorldModMcp.Indexing.Model;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 符號讀取器對真實 IL metadata 的行為。
///
/// <para>
/// 測試組件由 Roslyn 即時編出，所以這些斷言驗的是真的 metadata 解析，
/// 不是 mock。這比 Python 版強——那邊只能對 regex 或 tree-sitter 的文字解析測。
/// </para>
/// </summary>
public sealed class AssemblySymbolReaderTests : IDisposable
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Verse
        {
            public abstract class Def
            {
                public string defName;
                public string label { get; set; }
            }

            public abstract class BuildableDef : Def
            {
                public int costStuffCount;
            }

            public interface ITickable
            {
                void Tick();
            }

            public class ThingDef : BuildableDef, ITickable
            {
                public ThingDef() { }
                public ThingDef(string name) { defName = name; }

                public void Tick() { }

                public virtual bool CanEverPlaceOver(ThingDef other, int layer) => true;

                protected static List<ThingDef> All => new List<ThingDef>();

                private int hidden;
            }

            public static class ThingDefOf
            {
                public static ThingDef Steel;
            }

            public enum Rot { North, East }

            public struct IntVec3
            {
                public int x;
            }

            public class Repository<T> where T : Def
            {
                public T Get(string name) => default;
                public TOther Convert<TOther>(T source) => default;
            }
        }
        """;

    private readonly string _root;
    private readonly IReadOnlyList<SymbolRecord> _symbols;

    public AssemblySymbolReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-sym-" + Guid.NewGuid().ToString("n")[..12]);
        var path = SyntheticAssembly.Emit(Source, "Assembly-CSharp", _root);
        _symbols = new AssemblySymbolReader().Read(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SymbolRecord Find(string fqn, SymbolKind kind)
        => _symbols.Single(s => s.Fqn == fqn && s.Kind == kind);

    [Fact]
    public void AssemblyNameIsRecorded()
    {
        Assert.All(_symbols, s => Assert.Equal("Assembly-CSharp", s.Assembly));
    }

    [Fact]
    public void TypeKindsAreClassified()
    {
        Assert.Equal(SymbolKind.Class, Find("Verse.ThingDef", SymbolKind.Class).Kind);
        Assert.Equal(SymbolKind.Interface, Find("Verse.ITickable", SymbolKind.Interface).Kind);
        Assert.Equal(SymbolKind.Enum, Find("Verse.Rot", SymbolKind.Enum).Kind);
        Assert.Equal(SymbolKind.Struct, Find("Verse.IntVec3", SymbolKind.Struct).Kind);
    }

    /// <summary>
    /// 這是改寫的核心價值：完整的繼承鏈。
    /// Python 版的 parent_fqn 只有一層，回答不了「哪些類別繼承 ThingComp」這種
    /// RimWorld modding 的核心問題。
    /// </summary>
    [Fact]
    public void BaseChainIsComplete()
    {
        var thingDef = Find("Verse.ThingDef", SymbolKind.Class);

        Assert.Equal("Verse.BuildableDef|Verse.Def|System.Object", thingDef.BaseChain);
    }

    [Fact]
    public void InterfacesAreRecorded()
    {
        Assert.Equal("Verse.ITickable", Find("Verse.ThingDef", SymbolKind.Class).Interfaces);
    }

    /// <summary>簽章是真的組出來的型別簽章，不是「宣告那一行的原始文字」。</summary>
    [Fact]
    public void MethodSignatureCarriesRealTypes()
    {
        var method = Find("Verse.ThingDef.CanEverPlaceOver", SymbolKind.Method);

        Assert.Equal("bool CanEverPlaceOver(Verse.ThingDef other, int layer)", method.Signature);
        Assert.Equal("public", method.Accessibility);
        Assert.False(method.IsStatic);
    }

    [Fact]
    public void ConstructorsAreDistinguishedFromMethods()
    {
        var constructors = _symbols.Where(s => s.Kind == SymbolKind.Constructor && s.ParentFqn == "Verse.ThingDef").ToList();

        Assert.Equal(2, constructors.Count);
        Assert.Contains(constructors, c => c.Signature == "ThingDef()");
        Assert.Contains(constructors, c => c.Signature == "ThingDef(string name)");
    }

    [Fact]
    public void PropertiesAreNotDuplicatedAsAccessorMethods()
    {
        // get_label / set_label 是存取子，不應該另外以 method 出現。
        Assert.DoesNotContain(_symbols, s => s.ShortName is "get_label" or "set_label");

        var property = Find("Verse.Def.label", SymbolKind.Property);
        Assert.Equal("string label { get; set; }", property.Signature);
    }

    [Fact]
    public void FieldsCarryTypeAndAccessibility()
    {
        Assert.Equal("string defName", Find("Verse.Def.defName", SymbolKind.Field).Signature);
        Assert.Equal("private", Find("Verse.ThingDef.hidden", SymbolKind.Field).Accessibility);
    }

    /// <summary>
    /// ThingDefOf.Steel 這種靜態欄位是 RimWorld 最常見的 def 引用形式。
    /// 必須被索引到，Roslyn 的引用分析才有東西可比對。
    /// </summary>
    [Fact]
    public void StaticDefOfFieldsAreIndexed()
    {
        var field = Find("Verse.ThingDefOf.Steel", SymbolKind.Field);

        Assert.True(field.IsStatic);
        Assert.Equal("Verse.ThingDef Steel", field.Signature);
    }

    [Fact]
    public void StaticClassIsMarked()
    {
        var type = Find("Verse.ThingDefOf", SymbolKind.Class);

        Assert.True(type.IsStatic);
        Assert.Contains("static class", type.Signature, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericTypeParametersUseTheirDeclaredNames()
    {
        var repository = Find("Verse.Repository<T>", SymbolKind.Class);

        Assert.Equal("Verse.Repository<T>", repository.Fqn);
        Assert.Equal("Repository<T>", repository.ShortName);

        var get = Find("Verse.Repository<T>.Get", SymbolKind.Method);
        Assert.Equal("T Get(string name)", get.Signature);
    }

    [Fact]
    public void GenericMethodParametersAreResolved()
    {
        var convert = _symbols.Single(s => s.ShortName == "Convert" && s.Kind == SymbolKind.Method);

        Assert.Equal("TOther Convert<TOther>(T source)", convert.Signature);
    }

    [Fact]
    public void CompilerGeneratedTypesAreSkipped()
    {
        Assert.DoesNotContain(_symbols, s => s.ShortName.StartsWith('<'));
    }

    [Fact]
    public void MetadataTokensAreUsable()
    {
        // token 是後續「單一成員反編譯」的定位入口，必須非零且唯一。
        Assert.All(_symbols, s => Assert.NotEqual(0, s.MetadataToken));

        var tokens = _symbols.Select(s => s.MetadataToken).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void TypeSignatureShowsDirectInheritance()
    {
        var signature = Find("Verse.ThingDef", SymbolKind.Class).Signature;

        Assert.Equal("class Verse.ThingDef : Verse.BuildableDef, Verse.ITickable", signature);
    }

    [Fact]
    public void AbstractTypesAreMarked()
    {
        Assert.Contains("abstract class", Find("Verse.Def", SymbolKind.Class).Signature, StringComparison.Ordinal);
    }
}
