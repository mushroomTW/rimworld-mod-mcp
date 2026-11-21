using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Tools for inspecting the user's installed mods.</summary>
[McpServerToolType]
public sealed class ModTools(ModCatalog catalog)
{
    [McpServerTool(Name = "list_installed_mods", UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
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
        [Description("Include path, dependencies, load_after, force_load_after, force_load_before, incompatible_with, and supported_versions per mod.")]
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
        ForceLoadAfter = mod.ForceLoadAfter,
        ForceLoadBefore = mod.ForceLoadBefore,
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

    /// <summary>The six fields below are likewise omitted unless include_details is set.</summary>
    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Dependencies { get; init; }

    [JsonPropertyName("load_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? LoadAfter { get; init; }

    /// <summary>Hard ordering the game sorts by like load_after; official DLCs order themselves only through these.</summary>
    [JsonPropertyName("force_load_after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ForceLoadAfter { get; init; }

    [JsonPropertyName("force_load_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ForceLoadBefore { get; init; }

    [JsonPropertyName("incompatible_with")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? IncompatibleWith { get; init; }

    [JsonPropertyName("supported_versions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SupportedVersions { get; init; }
}
