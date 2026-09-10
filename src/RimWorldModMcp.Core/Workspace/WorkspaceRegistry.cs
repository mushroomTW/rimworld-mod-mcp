namespace RimWorldModMcp.Core.Workspace;

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
/// 持有本次啟動信任的 Mod 開發工作區，並強制執行寫入邊界。
///
/// <para>
/// 工作區只從啟動參數（<c>--workspace</c>）給定，不落地、不能在執行期擴張。
/// 信任範圍等於 MCP client 設定裡寫的那幾行——改設定即生效，沒有殘留狀態，
/// 也沒有任何工具能在對話中放寬邊界。這是本工具唯一的寫入授權來源。
/// </para>
/// </summary>
public sealed class WorkspaceRegistry
{
    private readonly IReadOnlyList<string> _roots;

    /// <summary>
    /// 每一條路徑都會被正規化；不存在的目錄直接拋出，讓設定錯誤在啟動時就浮現，
    /// 而不是等到第一次建置才以「不在工作區內」的形式出現。
    /// </summary>
    public WorkspaceRegistry(IEnumerable<string> roots)
    {
        var comparer = StringComparer.FromComparison(Platform.PathText.Comparison);
        var canonical = new SortedSet<string>(comparer);

        foreach (var root in roots)
        {
            var path = PathGuard.Canonicalize(root);

            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"工作區不是目錄：{path}");
            }

            canonical.Add(path);
        }

        _roots = [.. canonical];
    }

    /// <summary>目前信任且仍然存在的工作區根目錄。</summary>
    public IReadOnlyList<string> Roots()
    {
        // 啟動後被刪掉的目錄直接濾掉，不要讓失效的登記卡住後續判定。
        return [.. _roots.Where(Directory.Exists)];
    }

    /// <summary>
    /// 驗證一個 Mod 路徑落在某個信任的工作區內，回傳帶型別的驗證結果。
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
            if (PathGuard.IsWithin(candidate, root))
            {
                return new ValidatedModPath(candidate);
            }
        }

        throw new UnauthorizedAccessException(OutsideWorkspaceMessage);
    }

    /// <summary>
    /// 邊界違規的提示。信任範圍不能在對話中擴張，所以訊息指向的是設定檔，
    /// 而不是某個可以補呼叫的工具。
    /// </summary>
    internal const string OutsideWorkspaceMessage =
        "模組路徑不在信任的工作區內。工作區由 MCP client 設定的啟動參數 --workspace <路徑> 給定，請修改設定後重新啟動。";
}
