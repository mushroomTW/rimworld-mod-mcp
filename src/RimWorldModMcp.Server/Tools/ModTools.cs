using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Tools for inspecting the user's installed mods.</summary>
[McpServerToolType]
public sealed class ModTools(ModCatalog catalog, ModInspectionService inspection)
{
    [McpServerTool(Name = "list_installed_mods", UseStructuredContent = true, ReadOnly = true)]
    [Description("Find installed mods (local and Workshop; Core/DLC only with include_builtin): package_id matches a packageId or name fragment, so resolve a mod name here instead of paging the full list. Path, dependencies and versions only with include_details.")]
    public ListModsResult ListInstalledMods(
        [Description("Also list Core and installed DLCs.")]
        bool include_builtin = false,
        [Description("Filter by packageId or name substring (case-insensitive).")]
        string? package_id = null,
        [Description("Maximum results, 1-1000.")]
        int limit = 50,
        [Description("Skip the first N matches.")]
        int offset = 0,
        [Description("Include path, dependencies, load_after, incompatible_with, and supported_versions per mod.")]
        bool include_details = false) => ToolGuard.Run(() =>
    {
        var mods = catalog.Installed().ToList();

        if (include_builtin)
        {
            mods.AddRange(catalog.BuiltinPacks());
        }

        if (!string.IsNullOrWhiteSpace(package_id))
        {
            var needle = package_id.Trim();
            mods = [.. mods.Where(m =>
                m.PackageId.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))];
        }

        var total = mods.Count;
        var safeOffset = Math.Max(offset, 0);
        var safeLimit = Math.Clamp(limit, 1, 1000);
        var page = mods.Skip(safeOffset).Take(safeLimit).ToList();

        return new ListModsResult
        {
            Results = [.. page.Select(m => include_details ? ToSummary(m) : ToBrief(m))],
            Count = page.Count,
            Total = total,
            Offset = safeOffset,
            LimitReached = safeOffset + page.Count < total,
        };
    });

    [McpServerTool(Name = "inspect_installed_mod", UseStructuredContent = true)]
    [Description("Make an installed mod's C# queryable: decompiles and indexes every DLL synchronously on first use (minutes for large mods, which can exceed your tool-call timeout; search_source with package_id does the same in the background), then lists its assemblies with index keys and counts. Afterwards read_symbol, list_symbols, find_descendants, search_source and read_source_file cover the mod; pass an assembly key fragment such as 1.6/ to scope to one version.")]
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
                Assembly = a.Key,
                Path = a.Path,
                SymbolCount = a.SymbolCount,
                SourceFileCount = a.SourceFileCount,
                FromCache = a.FromCache,
            })],
            AssemblyCount = assemblies.Count,
        };
    });

    /// <summary>清單用：沒有路徑、相依與版本欄位。800 個 Mod 的清單帶全部欄位會直接爆量，
    /// 而路徑光是 Workshop 前綴就佔掉每列一半的 token，選 companion_mods 只需要 packageId。</summary>
    private static ModSummary ToBrief(ModInfo mod) => new()
    {
        PackageId = mod.PackageId,
        Name = mod.Name,
        Source = mod.Source,
    };

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

/// <summary>Installed mod listing. Count is this page's size; Total is the filtered total.</summary>
public sealed record ListModsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<ModSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("total")]
    public required int Total { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }
}

/// <summary>One mod's summary.</summary>
public sealed record ModSummary
{
    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Omitted from list_installed_mods unless include_details is set.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    /// <summary>local, workshop, core, or expansion.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>The four fields below are likewise omitted unless include_details is set.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Dependencies { get; init; }

    [JsonPropertyName("load_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LoadAfter { get; init; }

    [JsonPropertyName("incompatible_with")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? IncompatibleWith { get; init; }

    [JsonPropertyName("supported_versions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SupportedVersions { get; init; }
}

/// <summary>Result of inspecting a mod.</summary>
public sealed record InspectModResult
{
    [JsonPropertyName("mod")]
    public required ModSummary Mod { get; init; }

    [JsonPropertyName("assemblies")]
    public required IReadOnlyList<ModAssemblySummary> Assemblies { get; init; }

    [JsonPropertyName("assembly_count")]
    public required int AssemblyCount { get; init; }
}

/// <summary>One assembly inside a mod.</summary>
public sealed record ModAssemblySummary
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Index key of the assembly (mod:＜packageId＞:＜relative DLL path＞); the query tools' assembly filter matches against it.</summary>
    [JsonPropertyName("assembly")]
    public required string Assembly { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("symbol_count")]
    public required int SymbolCount { get; init; }

    [JsonPropertyName("source_file_count")]
    public required int SourceFileCount { get; init; }

    /// <summary>True when the existing index was reused without re-decompiling.</summary>
    [JsonPropertyName("from_cache")]
    public required bool FromCache { get; init; }
}
