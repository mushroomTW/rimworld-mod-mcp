using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Building;

/// <summary>一筆編譯診斷。</summary>
public sealed record BuildDiagnostic(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("column")] int? Column);

/// <summary>建置結果。XML-only Mod 與 C# Mod 的欄位不同。</summary>
public sealed record BuildResult
{
    /// <summary><c>xml_only</c> 或 <c>csharp</c>。</summary>
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
    public IReadOnlyList<BuildDiagnostic> Diagnostics { get; init; } = [];

    [JsonPropertyName("error_count")]
    public int ErrorCount => Diagnostics.Count(d => d.Severity == "error");

    [JsonPropertyName("warning_count")]
    public int WarningCount => Diagnostics.Count(d => d.Severity == "warning");

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// 驗證並建置 Mod。
///
/// <para>
/// 建置失敗時回傳<b>結構化診斷</b>（錯誤代碼、檔名、行列），而不是一整包 MSBuild 的
/// 文字輸出。Python 版回傳 <c>stdout[-12000:]</c>，呼叫端得自己從裡面猜哪裡錯了，
/// 而且那一萬多字元會直接佔掉呼叫端的 context。
/// </para>
/// </summary>
public sealed partial class BuildService(RimWorldLocator locator)
{
    public BuildResult Build(string modPath)
    {
        var mod = PathText.ResolveDirectory(modPath);

        ValidateAbout(mod);

        var sourceDirectory = Path.Combine(mod, "Source");

        // 不跟隨 symlink / junction，避免掃到工作區外或掉進自我指涉的連結。
        var projects = Directory.Exists(sourceDirectory)
            ? Directory.GetFiles(sourceDirectory, "*.csproj", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
            : [];

        if (projects.Length == 0)
        {
            return new BuildResult
            {
                Kind = "xml_only",
                Success = true,
                Mod = mod,
                Message = "XML-only Mod 結構有效，無需 C# 建置。",
            };
        }

        if (projects.Length > 1)
        {
            throw new InvalidOperationException("Source 下必須恰有一個 .csproj，才可安全建置。");
        }

        return BuildProject(mod, projects[0]);
    }

    /// <summary>MSBuild 卡死（NuGet 還原掛住等）時的停損點，避免整個 MCP server 無限期停擺。</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(10);

    private BuildResult BuildProject(string mod, string project)
    {
        var managed = locator.Detect().ManagedDir;

        DotnetHost.BuildExecution execution;

        try
        {
            execution = DotnetHost.RunBuild(
                project,
                workingDirectory: mod,
                managedDirectory: managed,
                extraArgs: ["-consoleLoggerParameters:NoSummary;ForceNoAlign"],
                timeout: BuildTimeout);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"dotnet build 超過 {BuildTimeout.TotalMinutes:0} 分鐘未完成，已強制終止。");
        }

        var stdout = execution.Stdout;
        var stderr = execution.Stderr;
        var success = execution.Success;
        var diagnostics = ParseDiagnostics(execution.CombinedOutput);

        var frameworks = TargetFrameworks(project);
        var (outputDirectory, deployed) = success ? Deploy(mod, project, frameworks) : (null, []);

        var warnings = new List<string>();

        if (success && deployed.Count == 0)
        {
            warnings.Add($"建置回報成功，但在輸出目錄下找不到任何 DLL；請確認 csproj 的 TargetFramework 與 OutputPath 設定。");
        }

        if (managed is null)
        {
            warnings.Add("未偵測到 RimWorld 的 Managed 目錄，遊戲組件參考可能無法解析。");
        }

        // 失敗但一筆診斷都沒解析到（MSBuild 崩潰、SDK 版本問題等非標準輸出）時，
        // 附上截斷過的輸出尾段——否則呼叫端拿到的是零資訊的 success:false。
        string? message = null;

        if (!success && diagnostics.Count == 0)
        {
            var combined = (stdout + Environment.NewLine + stderr).Trim();
            var tail = combined.Length > 4000 ? combined[^4000..] : combined;
            message = "建置失敗且無法解析出結構化診斷，原始輸出尾段：" + Utf8Text.Truncate(tail, 4000, out _);
        }

        return new BuildResult
        {
            Kind = "csharp",
            Success = success,
            Mod = mod,
            Project = project,
            TargetFramework = frameworks.Count > 0 ? frameworks[0] : null,
            OutputDirectory = outputDirectory,
            Deployed = deployed,
            Diagnostics = diagnostics,
            Warnings = warnings,
            Message = message,
        };
    }

    private static void ValidateAbout(string mod)
    {
        var aboutPath = Path.Combine(mod, "About", "About.xml");

        XDocument document;

        try
        {
            using var stream = File.OpenRead(aboutPath);
            document = XDocument.Load(stream);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            throw new InvalidOperationException($"About.xml 無效：{e.Message}");
        }

        if (document.Root?.Name.LocalName != "ModMetaData")
        {
            throw new InvalidOperationException("About.xml 的根節點必須是 ModMetaData。");
        }

        if (string.IsNullOrWhiteSpace(document.Root.Element("packageId")?.Value))
        {
            throw new InvalidOperationException("About.xml 缺少 packageId。");
        }
    }

    /// <summary>把建置產出的 DLL 部署到 Mod 的 Assemblies 目錄。</summary>
    private static (string? Directory, IReadOnlyList<string> Deployed) Deploy(
        string mod,
        string project,
        IReadOnlyList<string> frameworks)
    {
        foreach (var candidate in OutputDirectories(project, frameworks))
        {
            var libraries = Directory.Exists(candidate)
                ? Directory.GetFiles(candidate, "*.dll")
                : [];

            if (libraries.Length == 0)
            {
                continue;
            }

            var assemblies = Path.Combine(mod, "Assemblies");
            Directory.CreateDirectory(assemblies);

            var deployed = new List<string>();

            foreach (var library in libraries)
            {
                var destination = Path.Combine(assemblies, Path.GetFileName(library));
                File.Copy(library, destination, overwrite: true);
                deployed.Add(Path.GetRelativePath(mod, destination).Replace('\\', '/'));
            }

            return (candidate, deployed);
        }

        return (null, []);
    }

    /// <summary>
    /// 輸出目錄的候選順序：csproj 宣告的 TFM、bin/Release 本身、bin/Release 的子目錄。
    /// 不可退回硬編碼 net472——那會讓宣告其他 TFM 的專案找不到產出。
    /// </summary>
    private static IEnumerable<string> OutputDirectories(string project, IReadOnlyList<string> frameworks)
    {
        var releaseRoot = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release");

        foreach (var framework in frameworks)
        {
            yield return Path.Combine(releaseRoot, framework);
        }

        // 設了 AppendTargetFrameworkToOutputPath=false 的專案（腳手架產生的就是）
        // 會直接輸出在這一層。這一項必須排在子目錄掃描之前——SDK 預設會產生
        // bin/Release/ref/（reference assembly，方法沒有實作），先掃子目錄
        // 會把它部署進遊戲：載得起來但所有方法都是空的，極難診斷。
        yield return releaseRoot;

        if (Directory.Exists(releaseRoot))
        {
            foreach (var directory in Directory.GetDirectories(releaseRoot).Order())
            {
                var name = Path.GetFileName(directory);

                // reference assembly 目錄永遠不是部署對象。
                if (name.Equals("ref", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("refint", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return directory;
            }
        }
    }

    private static IReadOnlyList<string> TargetFrameworks(string project)
    {
        try
        {
            var document = XDocument.Load(project);

            foreach (var node in document.Descendants())
            {
                var name = node.Name.LocalName;

                if (name == "TargetFramework")
                {
                    return [node.Value.Trim()];
                }

                if (name == "TargetFrameworks")
                {
                    return [.. node.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                }
            }
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            // 讀不到就退回「掃 bin/Release 子目錄」的路徑。
        }

        return [];
    }

    /// <summary>從 MSBuild 輸出解析出結構化診斷。</summary>
    private static List<BuildDiagnostic> ParseDiagnostics(string output)
    {
        var diagnostics = new List<BuildDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var groups in DiagnosticRegex().Matches(output).Select(match => match.Groups))
        {
            var severity = groups["severity"].Value;
            var code = groups["code"].Value;
            var message = groups["message"].Value.Trim();
            var file = groups["file"].Success ? groups["file"].Value.Trim() : null;

            // MSBuild 會把同一筆診斷在摘要區重複輸出一次。
            var key = $"{file}:{groups["line"].Value}:{code}:{message}";

            if (!seen.Add(key))
            {
                continue;
            }

            diagnostics.Add(new BuildDiagnostic(
                severity,
                code,
                message,
                file,
                int.TryParse(groups["line"].Value, out var line) ? line : null,
                int.TryParse(groups["column"].Value, out var column) ? column : null));
        }

        return diagnostics;
    }

    // MSBuild 的標準診斷格式：file(line,col): severity CODE: message [project]
    [GeneratedRegex(
        @"^(?:(?<file>[^(\r\n]+?)\((?<line>\d+)(?:,(?<column>\d+))?\)\s*:\s*)?(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>[^\r\n]*?)(?:\s*\[[^\]\r\n]*\])?$",
        RegexOptions.Multiline)]
    private static partial Regex DiagnosticRegex();
}
