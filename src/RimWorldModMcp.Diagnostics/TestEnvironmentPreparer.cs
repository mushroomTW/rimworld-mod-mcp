using System.Text;
using System.Xml.Linq;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>準備完成的隔離環境。</summary>
public sealed record PreparedEnvironment(
    string SaveData,
    IReadOnlyList<TestLink> Links,
    IReadOnlyList<string> ActiveMods,
    BridgeState Bridge);

/// <summary>
/// 隔離環境的準備與清理：暫存存檔目錄、Mods 目錄下的臨時連結、
/// 自產的 ModsConfig.xml、Prefs 複製、Bridge 建置與連結、bridge token。
///
/// <para>
/// 從 <see cref="TestCycleService"/> 拆出來的第二塊：所有「在檔案系統上
/// 佈置與拆除隔離環境」的操作集中在這裡，行程生命週期歸
/// <see cref="GameLauncher"/>，協調歸 TestCycleService。
/// </para>
/// </summary>
public sealed class TestEnvironmentPreparer(
    DirectoryLink links,
    StoreDirectories store,
    BridgeBuilder bridgeBuilder)
{
    public const string BridgePackageId = "rimworldmodmcp.bridge";

    /// <summary>
    /// 佈置一次測試場次的隔離環境。中途失敗會先拆掉已建立的連結與 token
    /// 再重拋——呼叫端拿不到部分建立的連結清單，回滾必須發生在這裡。
    /// </summary>
    public PreparedEnvironment Prepare(
        string runId,
        string modPath,
        string packageId,
        RimWorldPaths paths,
        IReadOnlyList<string> orderedActiveMods,
        string token,
        IReadOnlyList<string> seedConfigFiles)
    {
        var createdLinks = new List<TestLink>();

        try
        {
            CleanupOldSaveData();

            var saveData = Path.Combine(store.TestSaveDataDirectory, runId);
            var configDirectory = Path.Combine(saveData, "Config");
            Directory.CreateDirectory(configDirectory);

            string testFolderName;

            if (IsDirectlyUnderModsDirectory(modPath, paths.ModsDir!))
            {
                // 受測 Mod 本來就住在 Mods/ 底下，RimWorld 自己會掃到它；再建一個連結
                // 就是兩份同 packageId，遊戲端 ModLister 會直接報 Log.Error。
                testFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(modPath));
            }
            else
            {
                testFolderName = DirectoryLink.LinkPrefix + "Test-" + packageId;
                var testLink = Path.Combine(paths.ModsDir!, testFolderName);
                links.EnsureLink(testLink, modPath);
                createdLinks.Add(new TestLink(testLink, modPath));
            }

            var bridgeSource = BridgeDirectory();
            var bridgeState = new BridgeState { State = "unavailable", Reason = "找不到 Bridge 原始碼目錄。" };
            var activeMods = orderedActiveMods.ToList();

            if (bridgeSource is not null)
            {
                // Bridge 必須先建置成 DLL，否則 RimWorld 會載入一個沒有組件的空 Mod，
                // 診斷就只剩 Player.log 這一條路。
                var build = bridgeBuilder.Ensure(bridgeSource);

                if (build is { Success: true, ModDirectory: not null })
                {
                    var bridgeLink = Path.Combine(paths.ModsDir!, DirectoryLink.LinkPrefix + "Bridge");
                    links.EnsureLink(bridgeLink, build.ModDirectory);
                    createdLinks.Add(new TestLink(bridgeLink, build.ModDirectory));

                    activeMods.Add(BridgePackageId);
                    bridgeState = new BridgeState { State = "active", PackageId = BridgePackageId };
                }
                else
                {
                    // 不靜默降級：把原因寫進場次狀態，讓使用者知道為什麼只有 Player.log 診斷。
                    bridgeState = new BridgeState { State = "unavailable", Reason = build.Error };
                }
            }

            WriteModsConfig(configDirectory, paths.ModsConfig, activeMods);
            CopyPrefs(configDirectory, paths.PrefsXml);
            SeedConfig(configDirectory, seedConfigFiles, testFolderName);
            WriteBridgeToken(token);

            return new PreparedEnvironment(saveData, createdLinks, activeMods, bridgeState);
        }
        catch
        {
            // 佈置到一半失敗：使用者的 Mods 目錄不能留下指向工作區的孤兒連結。
            RemoveLinks(createdLinks);
            DeleteBridgeToken();
            throw;
        }
    }

    /// <summary>移除一條連結。語意見 <see cref="DirectoryLink.RemoveLink"/>。</summary>
    public LinkRemoval RemoveLink(TestLink link) => links.RemoveLink(link.Link, link.Target);

    /// <summary>回滾用：逐一移除，結果不回報（呼叫端正在處理更早的失敗）。</summary>
    public void RemoveLinks(IEnumerable<TestLink> toRemove)
    {
        foreach (var link in toRemove)
        {
            links.RemoveLink(link.Link, link.Target);
        }
    }

    /// <summary>寫入一次性 bridge token；在 Unix 上限制為僅擁有者可讀寫。</summary>
    private void WriteBridgeToken(string token)
    {
        File.WriteAllText(store.BridgeTokenFile, token, new UTF8Encoding(false));

        if (!OperatingSystem.IsWindows())
        {
            // Linux 的 ~/.local/share 預設 umask 下檔案是 0644，同機其他使用者
            // 可讀——而這個 token 授權整條 loopback 診斷通道。
            File.SetUnixFileMode(store.BridgeTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// 場次結束後 token 就該失效。留著它的話，任何讀得到這個檔的本機程序
    /// 都能在 daemon 存活期間持續灌入「診斷」——那些文字最終會進到
    /// LLM 呼叫端的 context，等於一條 prompt injection 投遞管道。
    /// </summary>
    public void DeleteBridgeToken()
    {
        try
        {
            File.Delete(store.BridgeTokenFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 刪不掉時 daemon 端的驗證仍以檔案內容為準，下一場次會覆寫它。
        }
    }

    /// <summary>找出 repo 內的 Bridge Mod 目錄。</summary>
    private static string? BridgeDirectory()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "bridge");

        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        // 開發時執行檔在 bin/Debug/net10.0 底下，往上找到 repo 根目錄。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var bridge = Path.Combine(directory.FullName, "bridge");

            if (File.Exists(Path.Combine(bridge, "About", "About.xml")))
            {
                return bridge;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void WriteModsConfig(string configDirectory, string? existingConfig, IReadOnlyList<string> activeMods)
    {
        var (version, knownExpansions) = ReadConfigValues(existingConfig);

        var document = new XElement("ModsConfigData",
            new XElement("version", version),
            new XElement("activeMods", activeMods.Select(id => new XElement("li", id))),
            new XElement("knownExpansions", knownExpansions.Select(id => new XElement("li", id))));

        File.WriteAllText(
            Path.Combine(configDirectory, "ModsConfig.xml"),
            document.ToString(SaveOptions.None),
            new UTF8Encoding(false));
    }

    /// <summary>沿用使用者現有設定的遊戲版本與已知 DLC，避免遊戲把 DLC 當成新安裝。</summary>
    private static (string Version, IReadOnlyList<string> KnownExpansions) ReadConfigValues(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return ("1.6", []);
        }

        try
        {
            var root = XDocument.Load(path).Root;

            if (root is null)
            {
                return ("1.6", []);
            }

            var version = root.Element("version")?.Value.Trim();

            var expansions = (root.Element("knownExpansions")?.Elements("li") ?? [])
                .Select(e => e.Value.Trim().ToLowerInvariant())
                .Where(v => v.Length > 0)
                .ToList();

            return (string.IsNullOrEmpty(version) ? "1.6" : version, expansions);
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return ("1.6", []);
        }
    }

    private static void CopyPrefs(string configDirectory, string? prefsPath)
    {
        if (prefsPath is null || !File.Exists(prefsPath))
        {
            return;
        }

        try
        {
            // 以位元組複製保留原始編碼與 BOM。
            File.WriteAllBytes(Path.Combine(configDirectory, "Prefs.xml"), File.ReadAllBytes(prefsPath));
        }
        catch (IOException)
        {
            // 沒有 Prefs.xml 也能跑，只是遊戲會用預設值。
        }
    }

    /// <summary>
    /// 把呼叫端指定的設定檔（通常是受測 Mod 的 ModSettings）複製進隔離環境的 Config 目錄。
    /// 只寫 ModsConfig.xml 的話，每場測試都是「全新安裝」的設定值，
    /// 依賴特定設定才會觸發的路徑永遠測不到。
    /// </summary>
    private static void SeedConfig(string configDirectory, IReadOnlyList<string> files, string testFolderName)
    {
        foreach (var source in files)
        {
            var target = SeedFileName(Path.GetFileName(source), testFolderName);

            if (string.Equals(target, "ModsConfig.xml", StringComparison.OrdinalIgnoreCase))
            {
                // 啟用清單是隔離的核心，讓它被覆寫就等於沒隔離。
                throw new InvalidOperationException($"seed_config 不能覆寫 ModsConfig.xml：{source}");
            }

            File.Copy(source, Path.Combine(configDirectory, target), overwrite: true);
        }
    }

    /// <summary>
    /// RimWorld 以 <c>Mod_&lt;Mod 資料夾名&gt;_&lt;Mod 類別名&gt;.xml</c> 讀取 ModSettings，
    /// 而測試場次裡受測 Mod 的資料夾名是臨時連結名——從使用者正式環境複製來的
    /// 設定檔若不改名，遊戲會當作沒有設定。類別名取最後一個底線之後的部分。
    /// </summary>
    public static string SeedFileName(string fileName, string testFolderName)
    {
        const string prefix = "Mod_";

        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var stem = fileName[prefix.Length..^4];
        var split = stem.LastIndexOf('_');

        if (split <= 0 || split == stem.Length - 1)
        {
            return fileName;
        }

        return $"{prefix}{testFolderName}_{stem[(split + 1)..]}.xml";
    }

    /// <summary>
    /// 受測 Mod 是否直接位於 Mods 目錄下（父目錄就是 Mods）。
    /// 子目錄、Workshop 或其他地方都不算；大小寫依 <see cref="PathText.Comparison"/>，尾斜線忽略。
    /// </summary>
    public static bool IsDirectlyUnderModsDirectory(string modPath, string modsDir)
    {
        var mod = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath));
        var mods = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modsDir));
        var parent = Path.GetDirectoryName(mod);

        return parent is not null && string.Equals(parent, mods, PathText.Comparison);
    }

    /// <summary>清掉先前場次留下的暫存存檔目錄，避免無限累積。</summary>
    private void CleanupOldSaveData()
    {
        var root = store.TestSaveDataDirectory;

        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 還被佔用的就留著，下次再清。
            }
        }
    }
}
