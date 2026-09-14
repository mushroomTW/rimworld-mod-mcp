using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Tools for creating and building mods.</summary>
[McpServerToolType]
public sealed class WorkspaceTools(
    ModScaffolder scaffolder,
    BuildService builds)
{
    [McpServerTool(Name = "create_mod", UseStructuredContent = true, Idempotent = false)]
    [Description("Create a new mod skeleton with About.xml and the standard folders. Always start C# mods with with_code=true: the generated csproj is wired to the game assembly references that build_mod injects.")]
    public CreateModResult CreateMod(
        [Description("Existing directory in which to create the mod folder.")]
        string directory,
        [Description("Mod name; also used as the folder name.")]
        string name,
        [Description("RimWorld packageId: lowercase alphanumerics and . _ - only, e.g. yourname.yourmod.")]
        string package_id,
        [Description("Also generate a C# project skeleton (net472).")]
        bool with_code = false) => ToolGuard.Run(() =>
    {
        var path = scaffolder.Create(directory, name, package_id, with_code);

        return new CreateModResult
        {
            Mod = path,
            PackageId = package_id.Trim().ToLowerInvariant(),
            WithCode = with_code,
        };
    });

    [McpServerTool(Name = "build_mod", UseStructuredContent = true)]
    [Description("Validate an XML mod, or build a C# mod and deploy the DLL to Assemblies. Failures return structured compiler diagnostics.")]
    public BuildResult BuildMod(
        [Description("Path to the mod directory.")]
        string path) => ToolGuard.Run(() => builds.Build(path));
}

/// <summary>Result of creating a mod.</summary>
public sealed record CreateModResult
{
    [JsonPropertyName("mod")]
    public required string Mod { get; init; }

    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("with_code")]
    public required bool WithCode { get; init; }
}
