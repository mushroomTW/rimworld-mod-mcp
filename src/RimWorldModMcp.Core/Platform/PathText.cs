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

    /// <summary>把收到的目錄路徑正規化成絕對路徑；不存在或不是目錄就拋出。</summary>
    public static string ResolveDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ExpandUser(path)));

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Path does not exist or is not a directory: {full}");
        }

        return full;
    }
}
