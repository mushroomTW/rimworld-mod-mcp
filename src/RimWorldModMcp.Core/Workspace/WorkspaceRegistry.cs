using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Core.Workspace;

/// <summary>已登記工作區的持久化格式。</summary>
internal sealed record WorkspaceState
{
    [JsonPropertyName("roots")]
    public List<string> Roots { get; init; } = [];
}

/// <summary>
/// 一條「已通過寫入邊界驗證」的 Mod 路徑。
///
/// <para>
/// 唯一的建構路徑是 <see cref="WorkspaceRegistry.AllowedMod"/>。所有會寫入
/// 檔案系統的服務 API 一律只接受這個型別、不收 <c>string</c>——新加的服務
/// 忘記驗證時編譯器會直接拒絕，而不是靠每個人記得呼叫授權點。
/// </para>
/// </summary>
public readonly record struct ValidatedModPath
{
    /// <summary>正規化後（含 symlink 解析）的絕對路徑。</summary>
    public string Value { get; }

    internal ValidatedModPath(string value) => Value = value;

    public override string ToString() => Value;
}

/// <summary>
/// 管理使用者登記的 Mod 開發工作區，並強制執行寫入邊界。
///
/// <para>
/// 只有位於已登記工作區內的路徑可以被修改。這是本工具唯一的寫入授權來源。
/// </para>
/// </summary>
public sealed class WorkspaceRegistry(StoreDirectories store)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>目前已登記且仍然存在的工作區根目錄。</summary>
    public IReadOnlyList<string> Roots()
    {
        var state = ReadState();

        // 已經被刪掉的目錄直接濾掉，不要讓失效的登記卡住後續判定。
        return [.. state.Roots.Where(Directory.Exists)];
    }

    /// <summary>登記一個工作區。重複登記同一個目錄是冪等的。</summary>
    public string Configure(string path)
    {
        var root = PathGuard.Canonicalize(path);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"工作區不是目錄：{root}");
        }

        var comparer = StringComparer.FromComparison(Platform.PathText.Comparison);
        var roots = new SortedSet<string>(Roots(), comparer) { root };

        var file = store.WorkspacesFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        AtomicJson.Write(file, new WorkspaceState { Roots = [.. roots] }, JsonOptions);

        return root;
    }

    /// <summary>
    /// 驗證一個 Mod 路徑落在某個已登記工作區內，回傳帶型別的驗證結果。
    /// 不在範圍內就拋出——這是寫入操作唯一的守門點，
    /// <see cref="ValidatedModPath"/> 只能從這裡產生。
    /// </summary>
    public ValidatedModPath AllowedMod(string path)
    {
        var candidate = PathGuard.Canonicalize(path);

        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException($"路徑不存在或不是目錄：{candidate}");
        }

        foreach (var root in Roots())
        {
            if (PathGuard.IsWithin(candidate, PathGuard.Canonicalize(root)))
            {
                return new ValidatedModPath(candidate);
            }
        }

        throw new UnauthorizedAccessException("模組路徑不在已登記的工作區內；請先呼叫 configure_workspace。");
    }

    private WorkspaceState ReadState()
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceState>(File.ReadAllText(store.WorkspacesFile)) ?? new WorkspaceState();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // 讀不到或內容壞掉一律當成「還沒登記過任何工作區」，不要讓工具整個無法啟動。
            return new WorkspaceState();
        }
    }
}
