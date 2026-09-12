using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Workspace;

/// <summary>把工具收到的目錄路徑正規化成絕對路徑；不存在或不是目錄就拋出。</summary>
public static class ModDirectory
{
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PathText.ExpandUser(path)));

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"路徑不存在或不是目錄：{full}");
        }

        return full;
    }
}
