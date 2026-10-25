using System.Text.Json;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>為合成遊戲目錄建立執行期組件清單。</summary>
internal static class GameAssemblyManifest
{
    private const string TargetName = ".NETCoreApp,Version=v10.0/win-x64";

    public static void Write(string managedDirectory, params string[] assemblies)
    {
        var target = new Dictionary<string, object>();
        var libraries = new Dictionary<string, object>();

        foreach (var assembly in assemblies)
        {
            var package = Path.GetFileNameWithoutExtension(assembly) + "/1.0.0";
            target[package] = new
            {
                runtime = new Dictionary<string, object> { [assembly] = new object() },
            };
            libraries[package] = new { type = "package" };
        }

        const string runtimePack = "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0";
        target[runtimePack] = new
        {
            runtime = new Dictionary<string, object> { ["System.Private.CoreLib.dll"] = new object() },
        };
        libraries[runtimePack] = new { type = "runtimepack" };

        var manifest = new
        {
            runtimeTarget = new { name = TargetName, signature = string.Empty },
            targets = new Dictionary<string, object> { [TargetName] = target },
            libraries,
        };

        File.WriteAllText(
            Path.Combine(managedDirectory, "RimWorld.deps.json"),
            JsonSerializer.Serialize(manifest));
    }
}
