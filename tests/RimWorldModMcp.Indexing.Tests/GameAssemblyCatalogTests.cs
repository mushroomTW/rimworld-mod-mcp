using System.Text.Json;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>驗證執行期組件清單（.deps.json 與舊版掃描）的解析行為。</summary>
public sealed class GameAssemblyCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-assembly-catalog-" + Guid.NewGuid().ToString("n")[..12]);

    public GameAssemblyCatalogTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public void Find_NoDepsJson_FallsBackToLegacyPattern()
    {
        var main = Path.Combine(_root, "Assembly-CSharp.dll");
        var firstpass = Path.Combine(_root, "Assembly-CSharp-firstpass.dll");
        var unrelated = Path.Combine(_root, "Unrelated.dll");

        File.WriteAllText(main, "");
        File.WriteAllText(firstpass, "");
        File.WriteAllText(unrelated, "");

        var assemblies = GameAssemblyCatalog.Find(_root);

        Assert.Equal(2, assemblies.Count);
        Assert.Equal(firstpass, assemblies[0]);
        Assert.Equal(main, assemblies[1]);
    }

    [Test]
    public void Find_ValidDepsJson_ReturnsAssembliesExcludingRuntimePack()
    {
        File.WriteAllText(Path.Combine(_root, "Assembly-CSharp.dll"), "");
        File.WriteAllText(Path.Combine(_root, "Waylith.dll"), "");
        File.WriteAllText(Path.Combine(_root, "System.Private.CoreLib.dll"), "");

        GameAssemblyManifest.Write(_root, "Assembly-CSharp.dll", "Waylith.dll");

        var assemblies = GameAssemblyCatalog.Find(_root);

        Assert.Equal(2, assemblies.Count);
        Assert.Equal(Path.Combine(_root, "Assembly-CSharp.dll"), assemblies[0]);
        Assert.Equal(Path.Combine(_root, "Waylith.dll"), assemblies[1]);
    }

    [Test]
    public void Find_DeclaredAssemblyMissingOnDisk_ThrowsFileNotFoundException()
    {
        File.WriteAllText(Path.Combine(_root, "Assembly-CSharp.dll"), "");
        GameAssemblyManifest.Write(_root, "Assembly-CSharp.dll", "MissingOnDisk.dll");

        var ex = Assert.Throws<FileNotFoundException>(() => GameAssemblyCatalog.Find(_root));

        Assert.Contains("MissingOnDisk.dll", ex.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Find_ManifestLacksAssemblyCSharp_ThrowsInvalidDataException()
    {
        File.WriteAllText(Path.Combine(_root, "Other.dll"), "");
        GameAssemblyManifest.Write(_root, "Other.dll");

        var ex = Assert.Throws<InvalidDataException>(() => GameAssemblyCatalog.Find(_root));

        Assert.Contains("Assembly-CSharp.dll", ex.Message, StringComparison.Ordinal);
    }

    [Test]
    public void Find_InvalidManifestStructure_ThrowsInvalidDataException()
    {
        File.WriteAllText(Path.Combine(_root, "RimWorld.deps.json"), "{}");

        Assert.Throws<InvalidDataException>(() => GameAssemblyCatalog.Find(_root));
    }

    [Test]
    public void Find_LibraryWithNoRuntimeOrNonObjectRuntime_IsSkipped()
    {
        File.WriteAllText(Path.Combine(_root, "Assembly-CSharp.dll"), "");

        const string targetName = ".NETCoreApp,Version=v10.0/win-x64";
        var manifest = new
        {
            runtimeTarget = new { name = targetName, signature = "" },
            targets = new Dictionary<string, object>
            {
                [targetName] = new Dictionary<string, object>
                {
                    ["Assembly-CSharp/1.0.0"] = new
                    {
                        runtime = new Dictionary<string, object> { ["Assembly-CSharp.dll"] = new object() }
                    },
                    ["NoRuntime/1.0.0"] = new
                    {
                        dependencies = new Dictionary<string, string> { ["Foo"] = "1.0.0" }
                    },
                    ["StringRuntime/1.0.0"] = new
                    {
                        runtime = "not-an-object"
                    }
                }
            },
            libraries = new Dictionary<string, object>
            {
                ["Assembly-CSharp/1.0.0"] = new { type = "package" },
                ["NoRuntime/1.0.0"] = new { type = "package" },
                ["StringRuntime/1.0.0"] = new { type = "package" }
            }
        };

        File.WriteAllText(
            Path.Combine(_root, "RimWorld.deps.json"),
            JsonSerializer.Serialize(manifest));

        var assemblies = GameAssemblyCatalog.Find(_root);

        Assert.Equal(1, assemblies.Count);
        Assert.Equal(Path.Combine(_root, "Assembly-CSharp.dll"), assemblies[0]);
    }
}
