using System.Text.RegularExpressions;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Paths;

/// <summary>在三個平台上尋找 RimWorld 的安裝位置、設定檔與 Player.log。</summary>
public sealed partial class RimWorldLocator
{
    public const string SteamAppId = "294100";

    /// <summary>與 bridge/Source/BridgeMod.cs 的預設埠必須一致（Bridge 無法參考 Core，該處為複製的常數）。</summary>
    public const int DefaultBridgePort = 49460;

    private const string RimWorldFolderName = "RimWorld by Ludeon Studios";
    private const string SteamDirName = "Steam";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI instance service method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "DI instance service method")]
    public int BridgePort()
    {
        var raw = EnvironmentVariables.BridgePort?.Trim();

        return int.TryParse(raw, out var port) && port > 0 && port < 65536
            ? port
            : DefaultBridgePort;
    }

    private readonly Lock _cacheGate = new();
    private RimWorldPaths? _cachedPaths;
    private DateTime _cachedAtUtc;

    /// <summary>
    /// 偵測結果的短期快取。完整偵測要走 install candidates、讀 libraryfolders.vdf、
    /// 做十幾次存在性檢查；此物件是 singleton，一次 run_test_cycle 會呼叫 Detect
    /// 四次以上。TTL 取短（安裝路徑幾乎不變，但使用者可能剛裝好遊戲）。
    /// </summary>
    private static readonly TimeSpan DetectCacheTtl = TimeSpan.FromSeconds(5);

    public RimWorldPaths Detect()
    {
        lock (_cacheGate)
        {
            if (_cachedPaths is not null && DateTime.UtcNow - _cachedAtUtc < DetectCacheTtl)
            {
                return _cachedPaths;
            }
        }

        var detected = DetectUncached();

        lock (_cacheGate)
        {
            _cachedPaths = detected;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return detected;
    }

    private static RimWorldPaths DetectUncached()
    {
        var install = InstallCandidates().FirstOrDefault(Directory.Exists);
        if (install is null)
        {
            return RimWorldPaths.Empty;
        }

        var (managedDir, executable, dataDir) = PlatformLayout(install);

        // 每個衍生欄位都要各自通過存在性驗證，任一項不成立就是 null，
        // 呼叫端才能明確知道缺的是哪一塊。
        var validManaged = Directory.Exists(managedDir) ? managedDir : null;
        var validExecutable = File.Exists(executable) ? executable : null;

        // Data 目錄光是存在不算數，必須真的有 Core/Defs 才是可用的遊戲資料。
        var validData = Directory.Exists(Path.Combine(dataDir, "Core", "Defs")) ? dataDir : null;

        var modsDir = Path.Combine(install, "Mods");
        var validMods = Directory.Exists(modsDir) ? modsDir : null;

        var modsConfig = UserConfigCandidates().FirstOrDefault(File.Exists);
        var prefs = modsConfig is null ? null : Path.Combine(Path.GetDirectoryName(modsConfig)!, "Prefs.xml");

        return new RimWorldPaths(
            InstallRoot: install,
            Executable: validExecutable,
            ManagedDir: validManaged,
            DataDir: validData,
            ModsDir: validMods,
            WorkshopDir: WorkshopDirectory(install),
            PlayerLog: PlayerLogCandidates().FirstOrDefault(File.Exists),
            ModsConfig: modsConfig,
            PrefsXml: prefs is not null && File.Exists(prefs) ? prefs : null);
    }

    /// <summary>
    /// Workshop 內容目錄是從安裝路徑往上推導的：
    /// <c>&lt;lib&gt;/steamapps/common/RimWorld</c> 往上兩層到 <c>&lt;lib&gt;/steamapps</c>，
    /// 再進 <c>workshop/content/294100</c>。
    /// 若使用者用環境變數指定了不在 Steam 結構下的路徑，這個推導會落空而回傳 null——這是既有行為。
    /// </summary>
    private static string? WorkshopDirectory(string install)
    {
        var steamApps = Directory.GetParent(install)?.Parent?.FullName;
        if (steamApps is null)
        {
            return null;
        }

        var workshop = Path.Combine(steamApps, "workshop", "content", SteamAppId);
        return Directory.Exists(workshop) ? workshop : null;
    }

    private static (string ManagedDir, string Executable, string DataDir) PlatformLayout(string install)
    {
        if (OperatingSystem.IsWindows())
        {
            return (
                Path.Combine(install, "RimWorldWin64_Data", "Managed"),
                Path.Combine(install, "RimWorldWin64.exe"),
                Path.Combine(install, "Data"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return (
                Path.Combine(install, "RimWorldMac.app", "Contents", "Resources", "Data", "Managed"),
                MacExecutable(install),
                // macOS 的 Data 目錄在安裝根目錄下，不在 app bundle 內。
                Path.Combine(install, "Data"));
        }

        return (
            Path.Combine(install, "RimWorldLinux_Data", "Managed"),
            Path.Combine(install, "RimWorldLinux"),
            Path.Combine(install, "Data"));
    }

    /// <summary>
    /// macOS 的執行檔名稱不固定，依序嘗試：Info.plist 的 CFBundleExecutable、
    /// 兩個已知的硬編碼名稱、最後才是「MacOS 目錄下恰好只有一個檔案」的情況。
    /// </summary>
    private static string MacExecutable(string install)
    {
        var contents = Path.Combine(install, "RimWorldMac.app", "Contents");
        var macOsDir = Path.Combine(contents, "MacOS");
        var candidates = new List<string>();

        var plist = Path.Combine(contents, "Info.plist");
        if (File.Exists(plist))
        {
            try
            {
                var match = BundleExecutableRegex().Match(File.ReadAllText(plist));
                if (match.Success)
                {
                    candidates.Add(match.Groups[1].Value.Trim());
                }
            }
            catch (IOException)
            {
                // 讀不到 plist 就往下走硬編碼候選。
            }
        }

        candidates.Add(RimWorldFolderName);
        candidates.Add("RimWorldMac");

        foreach (var name in candidates.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var path = Path.Combine(macOsDir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        // 最後手段：只有在 MacOS 目錄恰好只有一個檔案時才敢採用，避免猜錯。
        if (Directory.Exists(macOsDir))
        {
            var only = Directory.GetFiles(macOsDir);
            if (only.Length == 1)
            {
                return only[0];
            }
        }

        return Path.Combine(macOsDir, "RimWorldMac");
    }

    private static IEnumerable<string> InstallCandidates()
    {
        var overridePath = EnvironmentVariables.GamePath;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var expanded = PathText.ExpandUser(overridePath);
            if (Directory.Exists(expanded))
            {
                yield return Path.GetFullPath(expanded);
            }
        }

        foreach (var library in LibraryRoots())
        {
            yield return Path.Combine(library, "steamapps", "common", "RimWorld");
        }
    }

    /// <summary>Steam library 可能散在多顆磁碟，要從 libraryfolders.vdf 展開。</summary>
    private static IEnumerable<string> LibraryRoots()
    {
        foreach (var steam in SteamRoots())
        {
            yield return steam;

            var manifest = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            string content;
            try
            {
                content = File.ReadAllText(manifest);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match match in LibraryPathRegex().Matches(content))
            {
                // VDF 以反斜線跳脫反斜線。
                var path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            // x86 優先：Steam 預設安裝在 Program Files (x86)。
            var x86 = Environment.GetEnvironmentVariable("PROGRAMFILES(X86)") ?? @"C:\Program Files (x86)";
            var native = Environment.GetEnvironmentVariable("PROGRAMFILES") ?? @"C:\Program Files";

            yield return Path.Combine(x86, SteamDirName);
            yield return Path.Combine(native, SteamDirName);
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", SteamDirName);
            yield break;
        }

        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", SteamDirName);
    }

    private static IEnumerable<string> UserConfigCandidates()
    {
        foreach (var root in ConfigRoots())
        {
            yield return Path.Combine(root, "Config", "ModsConfig.xml");
        }
    }

    private static IEnumerable<string> PlayerLogCandidates()
    {
        var overridePath = EnvironmentVariables.PlayerLog;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return PathText.ExpandUser(overridePath);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, "Library", "Logs", "Ludeon Studios", RimWorldFolderName, "Player.log");
            yield break;
        }

        foreach (var root in ConfigRoots())
        {
            yield return Path.Combine(root, "Player.log");
        }
    }

    private static IEnumerable<string> ConfigRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? home;
            yield return Path.Combine(profile, "AppData", "LocalLow", "Ludeon Studios", RimWorldFolderName);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");
            yield return Path.Combine(support, "RimWorld");
            yield return Path.Combine(support, RimWorldFolderName);
            yield break;
        }

        var unity = Path.Combine(home, ".config", "unity3d", "Ludeon Studios");
        yield return Path.Combine(unity, RimWorldFolderName);
        yield return Path.Combine(unity, "RimWorld");
    }

    [GeneratedRegex(@"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>")]
    private static partial Regex BundleExecutableRegex();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
