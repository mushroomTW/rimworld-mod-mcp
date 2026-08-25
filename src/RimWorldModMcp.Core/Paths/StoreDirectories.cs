namespace RimWorldModMcp.Core.Paths;

/// <summary>
/// 本工具自己的狀態與快取目錄。
///
/// <para>
/// Python 版用 platformdirs，在 Windows 會產生雙層的
/// <c>%LOCALAPPDATA%\RimWorldMcp\RimWorldMcp\Cache</c>。C# 版改用 .NET 的自然佈局：
/// <c>%LOCALAPPDATA%\RimWorldModMcp\</c> 單層，快取放 <c>Cache\</c> 子目錄。
/// </para>
/// <para>
/// 目錄一律 lazy 建立——光是建構這個物件不得產生檔案系統副作用，
/// 否則單純 import／載入組件就會在使用者磁碟上留下目錄。
/// </para>
/// </summary>
public sealed class StoreDirectories
{
    public const string AppName = "RimWorldModMcp";

    private readonly string _dataRoot;
    private readonly string _cacheRoot;

    public StoreDirectories()
        : this(DefaultDataRoot(), DefaultCacheRoot())
    {
    }

    /// <summary>測試用：把狀態與快取導向暫時目錄。</summary>
    public StoreDirectories(string dataRoot, string cacheRoot)
    {
        _dataRoot = dataRoot;
        _cacheRoot = cacheRoot;
    }

    /// <summary>狀態根目錄（工作區登記、鎖、測試狀態、診斷、快照）。</summary>
    public string DataHome => EnsureExists(_dataRoot);

    /// <summary>快取根目錄（索引資料庫、反編譯結果）。</summary>
    public string CacheHome => EnsureExists(_cacheRoot);

    public string WorkspacesFile => Path.Combine(DataHome, "workspaces.json");

    public string LocksDirectory => Path.Combine(DataHome, "locks");

    public string SnapshotsDirectory => Path.Combine(DataHome, "snapshots");

    public string TestStatusFile => Path.Combine(DataHome, "test-status.json");

    public string DaemonFile => Path.Combine(DataHome, "daemon.json");

    public string DiagnosticsFile => Path.Combine(DataHome, "diagnostics.json");

    public string BridgeTokenFile => Path.Combine(DataHome, "bridge-token");

    public string TestSaveDataDirectory => Path.Combine(DataHome, "test-savedata");

    public string IndexDirectory => Path.Combine(CacheHome, "index");

    public string IndexDatabaseFile => Path.Combine(IndexDirectory, "index.sqlite3");

    public string ModCacheDirectory => Path.Combine(CacheHome, "mods");

    private static string DefaultDataRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName)
        : Path.Combine(UnixHome(".local", "share"), AppName);

    private static string DefaultCacheRoot() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "Cache")
        : Path.Combine(UnixHome(".cache"), AppName);

    private static string UnixHome(params string[] fallbackSegments)
    {
        // macOS 走 Application Support / Caches，Linux 尊重 XDG 環境變數。
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return fallbackSegments[0] == ".cache"
                ? Path.Combine(home, "Library", "Caches")
                : Path.Combine(home, "Library", "Application Support");
        }

        var xdg = fallbackSegments[0] == ".cache"
            ? Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME");

        if (!string.IsNullOrWhiteSpace(xdg))
        {
            return xdg;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine([profile, .. fallbackSegments]);
    }

    private static string EnsureExists(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
