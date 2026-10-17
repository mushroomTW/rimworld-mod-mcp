using System.Runtime.InteropServices;
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

    /// <summary>安裝目錄、執行檔與 macOS app bundle 的遊戲名稱；Linux 的 apphost 沒有副檔名。</summary>
    private const string GameName = "RimWorld";

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

        var (managedDir, executable) = Layout(install);
        var dataDir = Path.Combine(install, "Data");

        // 每個衍生欄位都要各自通過存在性驗證，任一項不成立就是 null，
        // 呼叫端才能明確知道缺的是哪一塊。
        var validExecutable = File.Exists(executable) ? executable : null;

        // Data 目錄光是存在不算數，必須真的有 Core/Defs 才是可用的遊戲資料。
        var validData = Directory.Exists(Path.Combine(dataDir, "Core", "Defs")) ? dataDir : null;

        var modsDir = Path.Combine(install, "Mods");
        var validMods = Directory.Exists(modsDir) ? modsDir : null;

        var modsConfig = UserConfigCandidates().FirstOrDefault(File.Exists);
        var prefs = modsConfig is null ? null : Path.Combine(Path.GetDirectoryName(modsConfig)!, "Prefs.xml");

        return new RimWorldPaths(
            InstallRoot: install,
            Executable: managedDir is null ? null : validExecutable,
            ManagedDir: managedDir,
            DataDir: validData,
            ModsDir: validMods,
            WorkshopDir: WorkshopDirectory(install),
            PlayerLog: ResolvePlayerLog(),
            ModsConfig: modsConfig,
            PrefsXml: prefs is not null && File.Exists(prefs) ? prefs : null)
        {
            UnsupportedVersion = managedDir is null ? OlderThanSupported(install) : null,
        };
    }

    /// <summary>
    /// Workshop 內容目錄是從安裝路徑往上推導的：
    /// ＜lib＞/steamapps/common/RimWorld 往上兩層到 ＜lib＞/steamapps，
    /// 再進 workshop/content/294100。
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

    /// <summary>
    /// 本工具只支援 1.7：遊戲不再是 Unity 程式，改跑在 .NET 10 上，受控組件（Assembly-CSharp.dll、
    /// UnityStubs.dll 等）直接放在執行檔旁，不再有 *_Data/Managed。找不到 Assembly-CSharp.dll 時
    /// ManagedDir 為 null。Data/ 與 Mods/ 在所有平台都直接位於安裝根目錄。
    /// </summary>
    private static (string? ManagedDir, string Executable) Layout(string install)
    {
        if (OperatingSystem.IsMacOS())
        {
            return MacLayout(install);
        }

        // 執行檔是與 RimWorld.deps.json 同名的 apphost；
        // 只在 Windows 上實際驗證過，Linux 依 apphost 慣例推定為沒有副檔名的 RimWorld。
        var executable = Path.Combine(install, OperatingSystem.IsWindows() ? $"{GameName}.exe" : GameName);
        return (File.Exists(Path.Combine(install, "Assembly-CSharp.dll")) ? install : null, executable);
    }

    /// <summary>
    /// macOS 的受控組件在 app bundle 的 Contents/MacOS（未驗證：bundle 名稱不明，
    /// 所以在安裝根目錄的第一層 *.app 中找含 Assembly-CSharp.dll 的那個）。
    /// 執行檔取該目錄下 *.deps.json 去掉副檔名的名稱，找不到就退回 RimWorld。
    /// </summary>
    private static (string? ManagedDir, string Executable) MacLayout(string install)
    {
        string[] bundles;
        try
        {
            bundles = Directory.GetDirectories(install, "*.app");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bundles = [];
        }

        foreach (var bundle in bundles.Order(StringComparer.Ordinal))
        {
            var macOsDir = Path.Combine(bundle, "Contents", "MacOS");
            if (!File.Exists(Path.Combine(macOsDir, "Assembly-CSharp.dll")))
            {
                continue;
            }

            var deps = Directory.GetFiles(macOsDir, "*.deps.json").Order(StringComparer.Ordinal).FirstOrDefault();
            var name = deps is null ? GameName : Path.GetFileName(deps)[..^".deps.json".Length];
            return (macOsDir, Path.Combine(macOsDir, name));
        }

        return (null, Path.Combine(install, "RimWorld.app", "Contents", "MacOS", GameName));
    }

    /// <summary>
    /// 安裝目錄的版本低於 1.7 時回傳該版本（major.minor），否則 null。
    /// Version.txt 讀不到時，Unity 的 *_Data/Managed 結構本身就代表 1.7 以前的版本，回傳 "pre-1.7"。
    /// </summary>
    private static string? OlderThanSupported(string install)
    {
        var version = GameVersion.ReadMajorMinor(install);
        if (version is null)
        {
            return HasUnityLayout(install) ? "pre-1.7" : null;
        }

        var parts = version.Split('.');
        var major = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        return major < 1 || (major == 1 && minor < 7) ? version : null;
    }

    private static bool HasUnityLayout(string install)
        => Directory.Exists(Path.Combine(install, "RimWorldWin64_Data", "Managed"))
            || Directory.Exists(Path.Combine(install, "RimWorldLinux_Data", "Managed"))
            || Directory.Exists(Path.Combine(install, "RimWorldMac.app", "Contents", "Resources", "Data", "Managed"));

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
            yield return Path.Combine(library, "steamapps", "common", GameName);
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

    /// <summary>
    /// Logs/Player.log 要等遊戲第一次啟動才會建立。設定目錄已存在時先指向它，
    /// 否則第一場測試的 session 會把日誌路徑記成 null，整場都不讀日誌；
    /// 也不能退回同目錄下舊版留下的 Player.log，那份不會再被寫入。
    /// </summary>
    private static string? ResolvePlayerLog()
    {
        var candidates = PlayerLogCandidates().ToList();

        return candidates.FirstOrDefault(File.Exists)
            ?? candidates.FirstOrDefault(path => Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(path))));
    }

    /// <summary>
    /// 1.7 的日誌根目錄（RimWorldWaylith.Bootstrap.ResolveLogDataRoot）：macOS 另有位置，
    /// 其他平台就是資料根目錄。
    /// </summary>
    private static string DotNetLogRoot()
        => OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Ludeon Studios", RimWorldFolderName)
            : ConfigRoots().First();

    private static IEnumerable<string> PlayerLogCandidates()
    {
        var overridePath = EnvironmentVariables.PlayerLog;
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            yield return PathText.ExpandUser(overridePath);
        }

        // 日誌由新引擎寫在日誌根目錄的 Logs/ 底下（依據反編譯的啟動程式碼）。
        yield return Path.Combine(DotNetLogRoot(), "Logs", "Player.log");
    }

    private static IEnumerable<string> ConfigRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(LocalLowFolder(), "Ludeon Studios", RimWorldFolderName);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(home, "Library", "Application Support");
            yield return Path.Combine(support, GameName);
            yield return Path.Combine(support, RimWorldFolderName);
            yield break;
        }

        // 1.7 在 Linux 上遵循 XDG_CONFIG_HOME，而且只用「RimWorld by Ludeon Studios」這一個名稱。
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
        yield return Path.Combine(config, "unity3d", "Ludeon Studios", RimWorldFolderName);
    }

    private static readonly Guid LocalLowFolderId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    /// <summary>
    /// Windows 的 LocalLow。遊戲以 SHGetKnownFolderPath 取得，AppData 被重新導向或搬移時
    /// 不在 %USERPROFILE%\AppData\LocalLow；取不到才退回該預設位置。
    /// </summary>
    private static string LocalLowFolder()
    {
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(LocalLowFolderId, 0, 0, out var buffer) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(buffer) is { Length: > 0 } path)
                {
                    return path;
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        var profile = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "AppData", "LocalLow");
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathRegex();
}
