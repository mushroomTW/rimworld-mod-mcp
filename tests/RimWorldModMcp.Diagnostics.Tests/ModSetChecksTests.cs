using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>啟動測試場次前，對受測 Mod 與選集做的檢查。</summary>
public sealed class ModSetChecksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-modset-" + Guid.NewGuid().ToString("n")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Bridge 自帶 0Harmony.dll。受測 Mod 用了 Harmony 卻沒宣告 brrainz.harmony 時，
    /// 測試場次照樣能跑，玩家那邊卻會壞——必須明說。
    /// </summary>
    [Fact]
    public void ModReferencingHarmonyWithoutTheDependencyIsWarned()
    {
        var mod = ModReferencingHarmony();

        var warning = TestCycleService.HarmonyDependencyWarning(mod, ["ludeon.rimworld", "me.mod"]);

        Assert.NotNull(warning);
        Assert.Contains("brrainz.harmony", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonyInTheActiveSetSilencesTheWarning()
    {
        var mod = ModReferencingHarmony();

        Assert.Null(TestCycleService.HarmonyDependencyWarning(mod, ["ludeon.rimworld", "brrainz.harmony", "me.mod"]));
    }

    [Fact]
    public void ModWithoutHarmonyReferenceIsNotWarned()
    {
        var mod = Path.Combine(_root, "Plain");
        Emit("namespace Plain { public class P { } }", "Plain", Path.Combine(mod, "Assemblies"));

        Assert.Null(TestCycleService.HarmonyDependencyWarning(mod, ["ludeon.rimworld"]));
    }

    /// <summary>
    /// Mods/ 底下另有一份同 packageId 的本機副本時，RimWorld 只會載入其中一份並記 Log.Error，
    /// 測到的可能是舊副本。Workshop 副本不算（RimWorld 會給它加 _steam 後綴），
    /// 本工具自己的連結殘骸也不算。
    /// </summary>
    [Fact]
    public void OnlyOtherLocalCopiesCountAsDuplicates()
    {
        var workspace = Path.Combine(_root, "workspace", "MyMod");
        var oldCopy = Path.Combine(_root, "Mods", "MyMod-old");

        ModInfo[] installed =
        [
            Info(oldCopy, "local"),
            Info(Path.Combine(_root, "Mods", "RimWorldModMcp-Test-me.mod"), "local"),
            Info(Path.Combine(_root, "workshop", "123"), "workshop"),
            Info(workspace, "local"),
        ];

        var duplicates = TestCycleService.DuplicateLocalCopies("me.mod", workspace, installed);

        Assert.Equal([oldCopy], duplicates);
    }

    private static ModInfo Info(string path, string source) => new()
    {
        PackageId = "me.mod",
        Name = "My Mod",
        Path = path,
        Source = source,
    };

    private string ModReferencingHarmony()
    {
        var harmonyDir = Path.Combine(_root, "harmony");
        var harmony = Emit("namespace HarmonyLib { public class Harmony { public Harmony(string id) { } } }", "0Harmony", harmonyDir);

        var mod = Path.Combine(_root, "MyMod");
        Emit("namespace MyMod { public class Boot { public object H = new HarmonyLib.Harmony(\"me.mod\"); } }",
            "MyMod", Path.Combine(mod, "1.6", "Assemblies"), harmony);

        return mod;
    }

    private static string Emit(string source, string name, string directory, params string[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             .. references.Select(r => MetadataReference.CreateFromFile(r))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name + ".dll");
        var result = compilation.Emit(path);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}
