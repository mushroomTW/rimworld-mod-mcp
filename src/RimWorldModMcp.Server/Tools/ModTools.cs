using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>查看使用者已安裝的 Mod。</summary>
[McpServerToolType]
public sealed class ModTools(ModCatalog catalog, ModInspectionService inspection)
{
    [McpServerTool(Name = "list_installed_mods", UseStructuredContent = true, ReadOnly = true)]
    [Description("List all installed mods (local and Steam Workshop) with their dependencies.")]
    public ListModsResult ListInstalledMods(
        [Description("Also list Core and installed DLCs.")]
        bool include_builtin = false) => ToolGuard.Run(() =>
    {
        var mods = catalog.Installed().ToList();

        if (include_builtin)
        {
            mods.AddRange(catalog.BuiltinPacks());
        }

        return new ListModsResult
        {
            Results = [.. mods.Select(ToSummary)],
            Count = mods.Count,
        };
    });

    [McpServerTool(Name = "inspect_installed_mod", UseStructuredContent = true)]
    [Description("List an installed mod's assemblies with their symbol and source-file index counts; decompiles and indexes first if not yet indexed.")]
    public InspectModResult InspectInstalledMod(
        [Description("The mod's packageId.")]
        string package_id,
        [Description("Ignore the cache and re-index.")]
        bool force = false) => ToolGuard.Run(() =>
    {
        var mod = catalog.Find(package_id);
        var assemblies = inspection.Inspect(mod.PackageId, mod.Path, force);

        return new InspectModResult
        {
            Mod = ToSummary(mod),
            Assemblies = [.. assemblies.Select(a => new ModAssemblySummary
            {
                Name = a.Name,
                Path = a.Path,
                SymbolCount = a.SymbolCount,
                SourceFileCount = a.SourceFileCount,
                FromCache = a.FromCache,
            })],
            AssemblyCount = assemblies.Count,
        };
    });

    [McpServerTool(Name = "search_installed_mod_source", UseStructuredContent = true)]
    [Description("Regex search over an installed mod's decompiled source. Indexes it first if not yet indexed.")]
    public SearchSourceResult SearchInstalledModSource(
        [Description("The mod's packageId.")]
        string package_id,
        [Description("Regular expression (.NET syntax), always case-insensitive.")]
        string pattern,
        [Description("Maximum results, 1-800.")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        var mod = catalog.Find(package_id);
        var (hits, indexed) = inspection.SearchSource(mod.PackageId, mod.Path, pattern, limit);

        return new SearchSourceResult
        {
            Results = [.. hits.Select(h => new SourceMatch
            {
                Assembly = h.Assembly,
                File = h.File,
                Line = h.Line,
                Text = h.Text,
            })],
            Count = hits.Count,
            LimitReached = hits.Count >= Math.Clamp(limit, 1, 800),
            SourceIndexed = indexed,
        };
    });

    private static ModSummary ToSummary(ModInfo mod) => new()
    {
        PackageId = mod.PackageId,
        Name = mod.Name,
        Path = mod.Path,
        Source = mod.Source,
        Dependencies = mod.Dependencies,
        LoadAfter = mod.LoadAfter,
        IncompatibleWith = mod.IncompatibleWith,
        SupportedVersions = mod.SupportedVersions,
    };
}

/// <summary>已安裝 Mod 的清單。</summary>
public sealed record ListModsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<ModSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>一個 Mod 的摘要。</summary>
public sealed record ModSummary
{
    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary><c>local</c>、<c>workshop</c>、<c>core</c> 或 <c>expansion</c>。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("dependencies")]
    public required IReadOnlyList<string> Dependencies { get; init; }

    [JsonPropertyName("load_after")]
    public required IReadOnlyList<string> LoadAfter { get; init; }

    [JsonPropertyName("incompatible_with")]
    public required IReadOnlyList<string> IncompatibleWith { get; init; }

    [JsonPropertyName("supported_versions")]
    public required IReadOnlyList<string> SupportedVersions { get; init; }
}

/// <summary>Mod 檢視結果。</summary>
public sealed record InspectModResult
{
    [JsonPropertyName("mod")]
    public required ModSummary Mod { get; init; }

    [JsonPropertyName("assemblies")]
    public required IReadOnlyList<ModAssemblySummary> Assemblies { get; init; }

    [JsonPropertyName("assembly_count")]
    public required int AssemblyCount { get; init; }
}

/// <summary>Mod 中的一個組件。</summary>
public sealed record ModAssemblySummary
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("symbol_count")]
    public required int SymbolCount { get; init; }

    [JsonPropertyName("source_file_count")]
    public required int SourceFileCount { get; init; }

    /// <summary>true 代表沿用既有索引，沒有重新反編譯。</summary>
    [JsonPropertyName("from_cache")]
    public required bool FromCache { get; init; }
}
