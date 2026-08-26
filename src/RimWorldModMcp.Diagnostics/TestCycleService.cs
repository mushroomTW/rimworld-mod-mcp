using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 在隔離環境中啟動 RimWorld 測試開發中的 Mod。
///
/// <para>
/// 隔離靠三件事：<c>-savedatafolder</c> 指向暫存目錄（不碰使用者的存檔與設定）、
/// 自產的 ModsConfig.xml（只啟用這次要測的 Mod 與其相依）、
/// 以及 Mods 目錄下的臨時連結（不複製檔案，改動立即生效）。
/// </para>
/// </summary>
public sealed class TestCycleService(
    IRimWorldLocator locator,
    IProcessHost processes,
    IDirectoryLink links,
    WorkspaceRegistry workspaces,
    ModCatalog catalog,
    LoadOrderResolver resolver,
    StoreDirectories store,
    CriticalSectionLock locks,
    TestSessionStore sessions,
    DaemonBootstrapper daemons,
    BridgeBuilder bridgeBuilder,
    DiagnosticStore diagnostics)
{
    private const string BridgePackageId = "rimworldmodmcp.bridge";

    public TestSession Start(string modPath, IReadOnlyList<string>? companionMods, bool quickTest)
    {
        var mod = workspaces.AllowedMod(modPath);
        var paths = locator.Detect();

        if (paths.Executable is null || paths.ModsDir is null)
        {
            throw new DirectoryNotFoundException("找不到 RimWorld 的執行檔或 Mods 目錄。");
        }

        var info = AboutXml.Parse(mod, "workspace")
            ?? throw new InvalidOperationException("Mod 的 About/About.xml 無效或缺少 packageId。");

        var runId = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

        var available = new List<ModInfo>(catalog.BuiltinPacks());
        available.AddRange(catalog.Installed());
        available.Add(info);

        var selected = new List<ModInfo> { info };

        foreach (var companion in companionMods ?? [])
        {
            var match = available.FirstOrDefault(m => m.PackageId == companion.Trim().ToLowerInvariant());

            if (match is null)
            {
                throw new KeyNotFoundException($"找不到指定的相伴 Mod：{companion}");
            }

            selected.Add(match);
        }

        var order = resolver.Resolve(selected, available);

        if (order.Missing.Count > 0)
        {
            throw new InvalidOperationException($"缺少必要的相依 Mod：{string.Join("、", order.Missing)}");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var saveData = Path.Combine(store.TestSaveDataDirectory, runId);
        var configDirectory = Path.Combine(saveData, "Config");

        var testLink = Path.Combine(paths.ModsDir, DirectoryLink.LinkPrefix + "Test-" + info.PackageId);
        var bridgeLink = Path.Combine(paths.ModsDir, DirectoryLink.LinkPrefix + "Bridge");
        var createdLinks = new List<TestLink>();

        int? daemonPid = null;

        // 鎖只保護「檢查單一場次 → 建連結 → 寫設定 → 啟動遊戲」這段臨界區。
        // 場次開始之後改由狀態檔維持單一場次——否則忘記停止的場次會把鎖
        // 留在長壽的 MCP server 程序名下，殘骸回收永遠不會觸發。
        var lockToken = locks.Acquire("test", new Dictionary<string, string> { ["run_id"] = runId });

        try
        {
            // 單一場次的檢查必須在鎖之內。放在鎖之外的話，兩個並行的
            // run_test_cycle 會同時通過檢查、啟動兩個遊戲、互相覆寫狀態檔，
            // 其中一個的 PID 從此遺失成為孤兒。
            var active = sessions.Read();

            if (active.State == "running" && active.GamePid is { } pid && processes.IsAlive(pid))
            {
                throw new InvalidOperationException(
                    $"已有進行中的測試場次（run_id={active.RunId}）。請先呼叫 stop_test(confirm=true)。");
            }

            // 上一場次的遊戲崩潰時 Stop() 沒被呼叫，它的連結還躺在使用者的
            // Mods 目錄裡——狀態檔即將被覆寫，這是最後的清理機會。
            foreach (var stale in active.Links)
            {
                links.RemoveLink(stale.Link, stale.Target);
            }

            // 使用者自己開著遊戲時不能動 Mods 目錄——連結會被鎖住，而且會干擾他的存檔。
            if (IsGameRunning(paths.Executable))
            {
                throw new InvalidOperationException("RimWorld 正在執行中。請先關閉遊戲再開始測試。");
            }

            CleanupOldSaveData();
            Directory.CreateDirectory(configDirectory);

            links.EnsureLink(testLink, mod);
            createdLinks.Add(new TestLink(testLink, mod));

            var bridgeSource = BridgeDirectory();
            var bridgeState = new BridgeState { State = "unavailable", Reason = "找不到 Bridge 原始碼目錄。" };
            var activeMods = order.Active.ToList();

            if (bridgeSource is not null)
            {
                // Bridge 必須先建置成 DLL，否則 RimWorld 會載入一個沒有組件的空 Mod，
                // 診斷就只剩 Player.log 這一條路。
                var build = bridgeBuilder.Ensure(bridgeSource);

                if (build is { Success: true, ModDirectory: not null })
                {
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

            WriteBridgeToken(token);
            diagnostics.Clear();

            // daemon 必須在啟動遊戲之前就緒，否則最早的診斷會漏掉。
            var (daemonState, ownedPid) = daemons.Ensure();
            daemonPid = ownedPid;
            var daemonStartUtc = ownedPid is { } dp ? processes.StartTimeUtc(dp) : null;

            var logOffset = paths.PlayerLog is not null && File.Exists(paths.PlayerLog)
                ? new FileInfo(paths.PlayerLog).Length
                : 0;

            var session = new TestSession
            {
                State = "starting",
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Mod = mod,
                SaveData = saveData,
                ActiveMods = activeMods,
                SkippedLoadAfter = order.SkippedLoadAfter,
                Links = createdLinks,
                PlayerLog = paths.PlayerLog,
                LogOffset = logOffset,
                BridgePort = locator.BridgePort(),
                Bridge = bridgeState,
                Daemon = daemonState,
                DaemonPid = daemonPid,
                DaemonStartUtc = daemonStartUtc,
            };

            sessions.Write(session);

            using var game = LaunchGame(paths, saveData, quickTest, token);

            session = session with
            {
                State = "running",
                GamePid = game.Id,
                // 記下啟動時間，stop_test 終止前比對——PID 被作業系統重用時
                // 不做比對會殺掉一個無關的行程。
                GameStartUtc = processes.StartTimeUtc(game.Id),
            };
            sessions.Write(session);

            return session;
        }
        catch
        {
            // 任何一步失敗都要把已經建好的連結拆乾淨，否則使用者的 Mods 目錄
            // 會留下指向工作區的孤兒連結，遊戲會載入到半成品。
            foreach (var link in createdLinks)
            {
                links.RemoveLink(link.Link, link.Target);
            }

            if (daemonPid is { } owned)
            {
                processes.Terminate(owned);
            }

            DeleteBridgeToken();

            throw;
        }
        finally
        {
            locks.Release("test", lockToken);
        }
    }

    /// <summary>停止測試場次並清理。</summary>
    public TestSession Stop(bool terminateGame)
    {
        var session = sessions.Read();

        if (session.State is "idle" or "stopped")
        {
            return session;
        }

        // 順序很重要：必須先讓遊戲退出，才能移除連結。
        // RimWorld 執行中會透過這些連結載入 Mod 組件，此時刪除 reparse point
        // 會因為檔案被佔用而失敗，留下工具自己建立的孤兒連結。
        //
        // 只有明確要求時才終止遊戲。使用者自行啟動的 RimWorld 從不在此範圍內——
        // 那個行程的 PID 根本不會被記錄。啟動時間一併比對，防 PID 重用。
        var gameStopped = terminateGame
            && session.GamePid is { } gamePid
            && processes.Terminate(gamePid, session.GameStartUtc);

        if (gameStopped && session.GamePid is { } stoppedPid)
        {
            WaitForExit(stoppedPid, TimeSpan.FromSeconds(20));
        }

        var daemonStopped = session.DaemonPid is { } daemonPid
            && processes.Terminate(daemonPid, session.DaemonStartUtc);

        var remaining = new List<TestLink>();

        foreach (var link in session.Links)
        {
            // Removed 以外的結果都要留在狀態裡：Failed 是還被佔用（下次可補清），
            // NotOurs 是路徑被別的東西佔著——使用者需要知道 Mods 目錄有殘留。
            if (links.RemoveLink(link.Link, link.Target) != LinkRemoval.Removed)
            {
                remaining.Add(link);
            }
        }

        // 場次結束後 token 就該失效。留著它的話，任何讀得到這個檔的本機程序
        // 都能在 daemon 存活期間持續灌入「診斷」——那些文字最終會進到
        // LLM 呼叫端的 context，等於一條 prompt injection 投遞管道。
        DeleteBridgeToken();

        var gameStillAlive = session.GamePid is { } alivePid && processes.IsAlive(alivePid);

        if (session.SaveData is not null && Directory.Exists(session.SaveData) && !gameStillAlive)
        {
            // 遊戲還活著就跳過刪除。Windows 上檔案鎖會擋下來，但 POSIX 允許
            // 刪除開啟中的檔案——terminate_game=false（預設）時在 Linux/macOS
            // 會把執行中遊戲的存檔整個刪掉。
            try
            {
                Directory.Delete(session.SaveData, recursive: true);
            }
            catch (IOException)
            {
                // 檔案仍被鎖住；下次啟動的清理會處理掉。
            }
        }

        var stopped = new TestSession
        {
            State = "stopped",
            PreviousRun = session.RunId,
            Terminated = new TerminationResult { Daemon = daemonStopped, Game = gameStopped },
            // 沒能移除的連結要留在狀態裡，讓使用者知道 Mods 目錄還有殘留，
            // 也讓下一次 stop_test 有機會補清。
            Links = remaining,
        };

        sessions.Write(stopped);
        return stopped;
    }

    public TestSession Status() => sessions.Read();

    /// <summary>等待行程真正退出。Kill 是非同步的，立刻去刪檔案會撞上檔案佔用。</summary>
    private void WaitForExit(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!processes.IsAlive(processId))
            {
                // 行程樹完全放掉檔案控制代碼還需要一點時間。
                Thread.Sleep(500);
                return;
            }

            Thread.Sleep(200);
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

    private void DeleteBridgeToken()
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

    private Process LaunchGame(RimWorldPaths paths, string saveData, bool quickTest, string token)
    {
        var startInfo = new ProcessStartInfo(paths.Executable!)
        {
            WorkingDirectory = paths.InstallRoot,
            UseShellExecute = false,
            // 不重導 stdout/stderr：診斷本來就走 Player.log 與 Bridge，這兩條
            // 管線沒有人讀。重導又不讀的話，OS 管線緩衝區（Windows 約 4 KB）
            // 被 Unity 的啟動 log 填滿後，RimWorld 的下一次寫入會永久阻塞。
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false,
        };

        // 用 ArgumentList 而不是組字串：暫存目錄路徑含空格時，
        // 組字串會讓遊戲收到被拆開的參數。
        startInfo.ArgumentList.Add($"-savedatafolder={saveData}");

        if (quickTest)
        {
            startInfo.ArgumentList.Add("-quicktest");
        }

        startInfo.Environment["SteamAppId"] = RimWorldLocator.SteamAppId;
        startInfo.Environment["SteamGameId"] = RimWorldLocator.SteamAppId;
        startInfo.Environment[EnvironmentVariables.Name("BRIDGE_TOKEN")] = token;
        startInfo.Environment[EnvironmentVariables.Name("BRIDGE_PORT")] = locator.BridgePort().ToString();

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("無法啟動 RimWorld。");
    }

    private bool IsGameRunning(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);

        try
        {
            var found = Process.GetProcessesByName(name);

            // 回傳的每個 Process 都持有 handle，不 Dispose 會逐次洩漏。
            foreach (var process in found)
            {
                process.Dispose();
            }

            return found.Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
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
