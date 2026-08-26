namespace RimWorldModMcp.Core.Workspace;

/// <summary>
/// 判斷一個路徑是否落在允許寫入的根目錄之內。
///
/// <para>
/// 這是整個工具唯一的寫入邊界，所有會修改檔案的操作都必須先通過這裡。
/// </para>
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// 把路徑正規化成可安全比對的絕對路徑：展開 <c>~</c>、解析 <c>..</c>、
    /// <b>並且遞迴解析 symlink / junction</b>。
    ///
    /// <para>
    /// 最後那一步是關鍵。<see cref="Path.GetFullPath(string)"/> 只處理字串層面的
    /// <c>..</c>，不會解析 symlink——工作區裡放一個指向 <c>/etc</c> 的 symlink
    /// 就能通過單純的前綴比對。必須解析到真實目標之後再比對。
    /// </para>
    /// </summary>
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var expanded = ExpandUser(path);
        var full = Path.GetFullPath(expanded);

        try
        {
            var info = new DirectoryInfo(full);
            if (info.Exists)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null)
                {
                    full = resolved.FullName;
                }
            }
        }
        catch (IOException)
        {
            // 解析不了（斷掉的連結等）就用字串正規化的結果，
            // 後續的存在性檢查會把它擋下來。
        }

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>
    /// <paramref name="candidate"/> 是否等於 <paramref name="root"/> 或位於其下。
    ///
    /// <para>
    /// 以路徑分隔符為邊界比對，所以 <c>/workspace-evil</c> 不會被判定為
    /// 落在 <c>/workspace</c> 之內。
    /// </para>
    /// </summary>
    public static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var normalisedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        var normalisedRoot = Path.TrimEndingDirectorySeparator(root);

        if (string.Equals(normalisedCandidate, normalisedRoot, comparison))
        {
            return true;
        }

        var prefix = normalisedRoot + Path.DirectorySeparatorChar;
        return normalisedCandidate.StartsWith(prefix, comparison);
    }

    private static string ExpandUser(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
    }
}
