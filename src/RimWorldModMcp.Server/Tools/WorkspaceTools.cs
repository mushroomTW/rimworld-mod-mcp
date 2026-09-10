using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Mod 骨架建立與建置的工具。工作區由啟動參數 --workspace 給定，不在對話中登記。</summary>
[McpServerToolType]
public sealed class WorkspaceTools(
    WorkspaceRegistry workspaces,
    ModScaffolder scaffolder,
    BuildService builds)
{
    [McpServerTool(Name = "create_mod", UseStructuredContent = true, Idempotent = false)]
    [Description("在信任的工作區裡建立一個新 Mod 骨架，含 About.xml 與標準目錄結構。C# 骨架的 csproj 已設定好本工具建置時注入的遊戲組件參考，請用它而不是手寫。")]
    public CreateModResult CreateMod(
        [Description("工作區根目錄，必須是啟動參數 --workspace 給定的路徑本身。")]
        string workspace,
        [Description("Mod 名稱，也會作為資料夾名稱。")]
        string name,
        [Description("RimWorld 的 packageId，只能用小寫英數字與 . _ - ，例如 yourname.yourmod。")]
        string package_id,
        [Description("是否一併產生 C# 專案骨架（net472）。")]
        bool with_code = false) => ToolGuard.Run(() =>
    {
        var path = scaffolder.Create(workspace, name, package_id, with_code);

        return new CreateModResult
        {
            Mod = path,
            PackageId = package_id.Trim().ToLowerInvariant(),
            WithCode = with_code,
        };
    });

    [McpServerTool(Name = "build_mod", UseStructuredContent = true)]
    [Description("驗證 XML Mod，或建置 C# Mod 並把產出的 DLL 部署到 Assemblies。失敗時回傳結構化的編譯診斷。")]
    public BuildModResult BuildMod(
        [Description("Mod 目錄的路徑，必須在啟動參數 --workspace 給定的工作區內。")]
        string path) => ToolGuard.Run(() =>
    {
        var result = builds.Build(workspaces.AllowedMod(path));

        return new BuildModResult
        {
            Kind = result.Kind,
            Success = result.Success,
            Mod = result.Mod,
            Project = result.Project,
            TargetFramework = result.TargetFramework,
            OutputDirectory = result.OutputDirectory,
            Deployed = result.Deployed,
            Warnings = result.Warnings,
            Message = result.Message,
            Diagnostics = [.. result.Diagnostics.Select(d => new BuildDiagnosticSummary
            {
                Severity = d.Severity,
                Code = d.Code,
                Message = d.Message,
                File = d.File,
                Line = d.Line,
                Column = d.Column,
            })],
            ErrorCount = result.Diagnostics.Count(d => d.Severity == "error"),
            WarningCount = result.Diagnostics.Count(d => d.Severity == "warning"),
        };
    });
}

/// <summary>建立 Mod 的結果。</summary>
public sealed record CreateModResult
{
    [JsonPropertyName("mod")]
    public required string Mod { get; init; }

    [JsonPropertyName("package_id")]
    public required string PackageId { get; init; }

    [JsonPropertyName("with_code")]
    public required bool WithCode { get; init; }
}

/// <summary>建置結果。<c>kind</c> 為 <c>xml_only</c> 時只有前幾個欄位有值。</summary>
public sealed record BuildModResult
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("mod")]
    public required string Mod { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("target_framework")]
    public string? TargetFramework { get; init; }

    [JsonPropertyName("output_directory")]
    public string? OutputDirectory { get; init; }

    [JsonPropertyName("deployed")]
    public IReadOnlyList<string> Deployed { get; init; } = [];

    [JsonPropertyName("diagnostics")]
    public IReadOnlyList<BuildDiagnosticSummary> Diagnostics { get; init; } = [];

    [JsonPropertyName("error_count")]
    public int ErrorCount { get; init; }

    [JsonPropertyName("warning_count")]
    public int WarningCount { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>一筆編譯診斷。</summary>
public sealed record BuildDiagnosticSummary
{
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("column")]
    public int? Column { get; init; }
}
