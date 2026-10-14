using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using RimWorldModMcp.Indexing.Decompilation;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 1.7 沒有 UnityEngine.*.dll，只有 UnityStubs.dll（遊戲執行期以名稱重新導向）。
/// 對著 1.6 編譯的 Mod 參考的是 UnityEngine.CoreModule，反編譯時也要導向 UnityStubs，
/// 否則所有 Unity 型別都解析不到，結果塞滿「Unknown result type」。
/// 直接組出與 MemberDecompiler 相同的 resolver，不改行程共用的環境變數。
/// </summary>
public sealed class UnityStubsRedirectTests : IDisposable
{
    private const string UnitySource = """
        namespace UnityEngine
        {
            public struct Vector3
            {
                public float x;
                public Vector3 Scaled(float factor) => new Vector3 { x = x * factor };
            }
        }
        """;

    private const string ModSource = """
        namespace MyMod
        {
            public class Mover
            {
                public float Run(UnityEngine.Vector3 start)
                {
                    var moved = start.Scaled(2f);
                    return moved.Scaled(3f).x + moved.x;
                }
            }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-unity-stubs-" + Guid.NewGuid().ToString("n")[..12]);

    private string Game => Path.Combine(_root, "game");

    private string ModAssemblies => Path.Combine(_root, "mod", "1.6", "Assemblies");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ModBuiltAgainstUnityEngineResolvesTypesFromUnityStubs()
    {
        SyntheticAssembly.Emit(UnitySource, "UnityStubs", Game);

        var text = Decompile(EmitModAgainst(UnitySource, "UnityEngine.CoreModule", ModSource));

        Assert.Contains("Scaled(2f)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown result type", text, StringComparison.Ordinal);
    }

    /// <summary>Mod 自帶的真正 Unity.* 組件有 UnityStubs 沒有的型別，必須優先於導向。</summary>
    [Fact]
    public void ModShippedUnityAssemblyWinsOverStubs()
    {
        SyntheticAssembly.Emit(UnitySource, "UnityStubs", Game);
        var mathSource = UnitySource.Replace("namespace UnityEngine", "namespace Unity.Mathematics", StringComparison.Ordinal);
        var math = SyntheticAssembly.Emit(mathSource, "Unity.Mathematics", ModAssemblies);
        var mod = SyntheticAssembly.Emit(ModSource.Replace("UnityEngine.", "Unity.Mathematics.", StringComparison.Ordinal), "MyMod", ModAssemblies, math);

        var text = Decompile(mod);

        Assert.Contains("Scaled(2f)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown result type", text, StringComparison.Ordinal);
    }

    /// <summary>UnityStubs.dll 讀不了（例如遊戲更新中）時照常反編譯，只是 Unity 型別解析不到。</summary>
    [Fact]
    public void UnreadableStubsDoNotFailDecompilation()
    {
        Directory.CreateDirectory(Game);
        File.WriteAllBytes(Path.Combine(Game, UnityStubsResolver.StubsFileName), "not a PE file"u8.ToArray());

        var text = Decompile(EmitModAgainst(UnitySource, "UnityEngine.CoreModule", ModSource));

        Assert.Contains("class Mover", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UnityEngine", true)]
    [InlineData("UnityEngine.CoreModule", true)]
    [InlineData("Unity.Mathematics", true)]
    [InlineData("Assembly-CSharp-firstpass", true)]
    [InlineData("Unity.Newtonsoft.Json", false)]
    [InlineData("Assembly-CSharp", false)]
    [InlineData("UnityStubs", false)]
    [InlineData("unityengine.coremodule", false)]
    public void RedirectRulesMatchTheGame(string name, bool redirected)
        => Assert.Equal(redirected, UnityStubsResolver.IsRedirected(name));

    /// <summary>編譯期參考的組件放在任何搜尋路徑之外，與玩家的 1.7 安裝相同。</summary>
    private string EmitModAgainst(string referenceSource, string referenceName, string modSource)
    {
        var reference = SyntheticAssembly.Emit(referenceSource, referenceName, Path.Combine(_root, "build-refs"));
        return SyntheticAssembly.Emit(modSource, "MyMod", ModAssemblies, reference);
    }

    private string Decompile(string assemblyPath)
    {
        using var file = MemberDecompiler.OpenImage(assemblyPath);
        var resolver = new UniversalAssemblyResolver(
            assemblyPath,
            throwOnError: false,
            file.DetectTargetFrameworkId(),
            streamOptions: MemberDecompiler.ImageStreamOptions);
        resolver.AddSearchDirectory(Game);

        var decompiler = new CSharpDecompiler(
            file,
            UnityStubsResolver.Wrap(resolver, Game),
            new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });

        return decompiler.DecompileWholeModuleAsString();
    }
}
