namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// 路徑字串的共用小工具。
///
/// <para>
/// <see cref="Comparison"/> 與 <see cref="ExpandUser"/> 之前在四、五個檔案裡
/// 各自複製了一份，任何一份改了其他份不會跟上——集中到這裡。
/// </para>
/// </summary>
public static class PathText
{
    /// <summary>
    /// 路徑比對的大小寫語意：Linux 區分大小寫，Windows 與 macOS 預設不區分。
    /// </summary>
    public static StringComparison Comparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>把開頭的 <c>~</c> 展開成使用者家目錄。</summary>
    public static string ExpandUser(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~').TrimStart('/', '\\'));
    }
}
