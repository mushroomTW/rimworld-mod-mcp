using System.Text.Json;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>讀取 RimWorld 執行期宣告的遊戲組件。</summary>
internal static class GameAssemblyCatalog
{
    private const string LegacyAssemblyPattern = "Assembly-CSharp*.dll";

    /// <summary>
    /// 以 RimWorld 的 .deps.json 作為組件來源，並排除 .NET runtime pack。
    /// 沒有相依性清單時保留舊版 Assembly-CSharp 掃描行為。
    /// </summary>
    public static IReadOnlyList<string> Find(string managedDirectory)
    {
        var dependencyFile = Directory.GetFiles(managedDirectory, "*.deps.json")
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        if (dependencyFile is null)
        {
            return [.. Directory.GetFiles(managedDirectory, LegacyAssemblyPattern).Order(StringComparer.Ordinal)];
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(dependencyFile));
        var root = document.RootElement;

        if (!root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            || !runtimeTarget.TryGetProperty("name", out var targetNameElement)
            || targetNameElement.GetString() is not { Length: > 0 } targetName
            || !root.TryGetProperty("targets", out var targets)
            || !targets.TryGetProperty(targetName, out var target)
            || !root.TryGetProperty("libraries", out var libraries))
        {
            throw new InvalidDataException($"Invalid RimWorld dependency manifest: {dependencyFile}");
        }

        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in target.EnumerateObject())
        {
            if (libraries.TryGetProperty(library.Name, out var metadata)
                && metadata.TryGetProperty("type", out var libraryType)
                && string.Equals(libraryType.GetString(), "runtimepack", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!library.Value.TryGetProperty("runtime", out var runtimeAssets)
                || runtimeAssets.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var asset in runtimeAssets.EnumerateObject())
            {
                if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(asset.Name);
                var assemblyPath = Path.Combine(managedDirectory, fileName);

                if (!File.Exists(assemblyPath))
                {
                    throw new FileNotFoundException(
                        $"RimWorld declares runtime assembly '{fileName}' in '{dependencyFile}', but the file is missing.",
                        assemblyPath);
                }

                assemblies.TryAdd(fileName, assemblyPath);
            }
        }

        if (!assemblies.ContainsKey("Assembly-CSharp.dll"))
        {
            throw new InvalidDataException($"RimWorld dependency manifest does not declare Assembly-CSharp.dll: {dependencyFile}");
        }

        return [.. assemblies.Values.OrderBy(Path.GetFileName, StringComparer.Ordinal)];
    }
}
