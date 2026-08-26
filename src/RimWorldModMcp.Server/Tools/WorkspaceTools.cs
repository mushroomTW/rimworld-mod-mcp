using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Core.Assets;
using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Mod 開發工作區、建置與資產的工具。</summary>
[McpServerToolType]
public sealed class WorkspaceTools(
    WorkspaceRegistry workspaces,
    ModScaffolder scaffolder,
    CheckpointService checkpoints,
    BuildService builds,
    AssetService assets)
{
    [McpServerTool(Name = "configure_workspace", UseStructuredContent = true)]
    [Description("登記一個 Mod 開發工作區。只有已登記工作區內的路徑可以被修改，所以建立或修改 Mod 前必須先呼叫這個。")]
    public ConfigureWorkspaceResult ConfigureWorkspace(
        [Description("工作區根目錄的絕對路徑。")]
        string path)
    {
        var workspace = workspaces.Configure(path);

        return new ConfigureWorkspaceResult
        {
            Workspace = workspace,
            Registered = workspaces.Roots(),
        };
    }

    [McpServerTool(Name = "create_mod", UseStructuredContent = true, Idempotent = false)]
    [Description("在已登記的工作區裡建立一個新 Mod 骨架，含 About.xml 與標準目錄結構。")]
    public CreateModResult CreateMod(
        [Description("已登記的工作區根目錄。")]
        string workspace,
        [Description("Mod 名稱，也會作為資料夾名稱。")]
        string name,
        [Description("RimWorld 的 packageId，只能用小寫英數字與 . _ - ，例如 yourname.yourmod。")]
        string package_id,
        [Description("是否一併產生 C# 專案骨架（net472）。")]
        bool with_code = false)
    {
        var path = scaffolder.Create(workspace, name, package_id, with_code);

        return new CreateModResult
        {
            Mod = path,
            PackageId = package_id.Trim().ToLowerInvariant(),
            WithCode = with_code,
        };
    }

    [McpServerTool(Name = "build_mod", UseStructuredContent = true)]
    [Description("驗證 XML Mod，或建置 C# Mod 並把產出的 DLL 部署到 Assemblies。失敗時回傳結構化的編譯診斷。")]
    public BuildModResult BuildMod(
        [Description("Mod 目錄的路徑，必須在已登記的工作區內。")]
        string path)
    {
        var result = builds.Build(path);

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
    }

    [McpServerTool(Name = "create_checkpoint", UseStructuredContent = true, Idempotent = false)]
    [Description("為 Mod 建立一份本機快照，之後可以還原。建置產物與 .git 不會進快照。")]
    public CheckpointResult CreateCheckpoint(
        [Description("Mod 目錄的路徑。")]
        string path)
    {
        var checkpoint = checkpoints.Create(path);

        return new CheckpointResult
        {
            Id = checkpoint.Id,
            Path = checkpoint.Path,
            CreatedUtc = checkpoint.CreatedUtc,
        };
    }

    [McpServerTool(Name = "list_checkpoints", UseStructuredContent = true, ReadOnly = true)]
    [Description("列出一個 Mod 的所有快照，由新到舊。")]
    public ListCheckpointsResult ListCheckpoints(
        [Description("Mod 目錄的路徑。")]
        string path)
    {
        var items = checkpoints.List(path);

        return new ListCheckpointsResult
        {
            Results = [.. items.Select(c => new CheckpointResult { Id = c.Id, Path = c.Path, CreatedUtc = c.CreatedUtc })],
            Count = items.Count,
        };
    }

    [McpServerTool(Name = "restore_checkpoint", UseStructuredContent = true, Destructive = true)]
    [Description("把 Mod 還原到指定快照。這會覆蓋現有內容，所以需要 confirm=true；還原前會自動再建一份快照。")]
    public RestoreCheckpointResult RestoreCheckpoint(
        [Description("Mod 目錄的路徑。")]
        string path,
        [Description("要還原的快照 ID。")]
        string checkpoint_id,
        [Description("必須明確傳 true 才會執行。")]
        bool confirm = false)
    {
        if (!confirm)
        {
            throw new UnauthorizedAccessException("還原快照會覆蓋現有內容，需要 confirm=true。");
        }

        var (restored, safety) = checkpoints.Restore(path, checkpoint_id);

        return new RestoreCheckpointResult
        {
            Restored = restored,
            RestoredFrom = checkpoint_id,
            PreRestoreCheckpoint = safety,
        };
    }

    [McpServerTool(Name = "import_mod_asset", UseStructuredContent = true, Idempotent = false)]
    [Description("把圖片或音效複製到 Mod 的 Textures/Sounds 目錄，並回傳 RimWorld 使用的引用路徑。")]
    public ImportAssetResult ImportModAsset(
        [Description("Mod 目錄的路徑。")]
        string path,
        [Description("來源檔案的絕對路徑。")]
        string source_path,
        [Description("資產類別：texture 接受 png/jpg/jpeg，sound 接受 ogg/wav。")]
        AssetKind kind)
    {
        var result = assets.Import(path, source_path, kind);

        return new ImportAssetResult
        {
            Asset = result.Asset,
            Reference = result.Reference,
        };
    }

    [McpServerTool(Name = "validate_mod_assets", UseStructuredContent = true, ReadOnly = true)]
    [Description("檢查 Mod 內資產的副檔名、檔案簽名與大小。副檔名對但內容不符的檔案會讓遊戲載入失敗。")]
    public ValidateAssetsResult ValidateModAssets(
        [Description("Mod 目錄的路徑。")]
        string path)
    {
        var issues = assets.Validate(path);

        return new ValidateAssetsResult
        {
            Results = [.. issues.Select(i => new AssetIssueSummary
            {
                Level = i.Level,
                File = i.File,
                Message = i.Message,
            })],
            Count = issues.Count,
            ErrorCount = issues.Count(i => i.Level == "error"),
        };
    }
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

/// <summary>一份快照。</summary>
public sealed record CheckpointResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("created_utc")]
    public required DateTime CreatedUtc { get; init; }
}

/// <summary>快照清單。</summary>
public sealed record ListCheckpointsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<CheckpointResult> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }
}

/// <summary>還原快照的結果。</summary>
public sealed record RestoreCheckpointResult
{
    [JsonPropertyName("restored")]
    public required string Restored { get; init; }

    [JsonPropertyName("restored_from")]
    public required string RestoredFrom { get; init; }

    /// <summary>還原前自動建立的安全快照，可用來回到還原之前的狀態。</summary>
    [JsonPropertyName("pre_restore_checkpoint")]
    public required string PreRestoreCheckpoint { get; init; }
}

/// <summary>匯入資產的結果。</summary>
public sealed record ImportAssetResult
{
    [JsonPropertyName("asset")]
    public required string Asset { get; init; }

    /// <summary>RimWorld 引用這個資產時用的路徑（不含副檔名）。</summary>
    [JsonPropertyName("reference")]
    public required string Reference { get; init; }
}

/// <summary>資產檢查結果。</summary>
public sealed record ValidateAssetsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<AssetIssueSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("error_count")]
    public required int ErrorCount { get; init; }
}

/// <summary>一筆資產問題。</summary>
public sealed record AssetIssueSummary
{
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("file")]
    public required string File { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
