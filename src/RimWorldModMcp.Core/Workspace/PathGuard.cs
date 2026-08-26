using RimWorldModMcp.Core.Platform;

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

        var expanded = PathText.ExpandUser(path);
        var full = Path.GetFullPath(expanded);

        return Path.TrimEndingDirectorySeparator(ResolveRealPath(full, depth: 0));
    }

    /// <summary>
    /// 從根往下逐一解析每個路徑元件的 symlink / junction。
    ///
    /// <para>
    /// 只對葉節點呼叫 <see cref="FileSystemInfo.ResolveLinkTarget"/> 是不夠的：
    /// <c>工作區\link\子目錄</c> 這種「中間元件是連結」的路徑，葉節點本身不是
    /// reparse point，會原樣通過前綴比對——而 junction 在 Windows 上不需要
    /// 任何權限就建得出來。必須逐層解析。
    /// </para>
    /// </summary>
    private static string ResolveRealPath(string full, int depth)
    {
        // 連結指向連結可以構成迴圈；超過合理深度就放棄解析，
        // 用字串形式讓後續的存在性檢查去擋。
        if (depth > 40)
        {
            return full;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        var current = root;
        var segments = full[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            try
            {
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);

                if (info.Exists)
                {
                    var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (resolved is not null)
                    {
                        // 連結目標的路徑本身也可能含有中間連結，遞迴解析到底。
                        current = ResolveRealPath(resolved.FullName, depth + 1);
                    }
                }

                // 不存在的尾端元件無從解析，維持字串附加；
                // 後續的存在性檢查會把不存在的路徑擋下來。
            }
            catch (IOException)
            {
                // 解析不了（斷掉的連結等）就用字串正規化的結果。
            }
            catch (UnauthorizedAccessException)
            {
                // 無權讀取屬性時同樣退回字串形式，不能讓例外逃出邊界檢查。
            }
        }

        return current;
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
        var comparison = PathText.Comparison;

        var normalisedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        var normalisedRoot = Path.TrimEndingDirectorySeparator(root);

        if (string.Equals(normalisedCandidate, normalisedRoot, comparison))
        {
            return true;
        }

        var prefix = normalisedRoot + Path.DirectorySeparatorChar;
        return normalisedCandidate.StartsWith(prefix, comparison);
    }
}
