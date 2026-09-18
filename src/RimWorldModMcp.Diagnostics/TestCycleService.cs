using System.Buffers.Text;
using System.Security.Cryptography;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>解析完成的測試選集。</summary>
public sealed record TestModSet(
    IReadOnlyList<string> ActiveMods,
    IReadOnlyList<string> SkippedLoadAfter);

/// <summary>
/// 在隔離環境中啟動 RimWorld 測試開發中的 Mod。
///
/// <para>
/// 隔離靠三件事：<c>-savedatafolder</c> 指向暫存目錄（不碰使用者的存檔與設定）、
/// 自產的 ModsConfig.xml（只啟用這次要測的 Mod 與其相依）、
/// 以及 Mods 目錄下的臨時連結（不複製檔案，改動立即生效）。
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "DI orchestrator injecting required services")]
public sealed class TestCycleService(
    RimWorldLocator locator,
    IProcessHost processes,
    ModCatalog catalog,
    LoadOrderResolver loadOrders,
    TestEnvironmentPreparer environment,
    GameLauncher launcher,
    CriticalSectionLock locks,
    TestSessionStore sessions,
    DaemonBootstrapper daemons,
    DiagnosticStore diagnostics,
    GameStateStore gameState)
{
    public TestSession Start(
        string modPath,
        IReadOnlyList<string>? companionMods,
        bool quickTest,
        IReadOnlyList<string>? seedConfig = null,
        bool fullscreen = true)
    {
        var mod = PathText.ResolveDirectory(modPath);
        var seedFiles = ResolveSeedConfig(seedConfig);
        var paths = locator.Detect();

        if (paths.Executable is null || paths.ModsDir is null)
        {
            throw new DirectoryNotFoundException("RimWorld executable or Mods directory not found.");
        }

        var info = AboutXml.Parse(mod, "workspace")
            ?? throw new InvalidOperationException("The mod's About/About.xml is invalid or missing packageId.");

        var runId = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var modSet = ResolveModSet(info, companionMods);

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

        // 鎖只保護「檢查單一場次 → 佈置環境 → 啟動遊戲」這段臨界區。
        // 場次開始之後改由狀態檔維持單一場次——否則忘記停止的場次會把鎖
        // 留在長壽的 MCP server 程序名下，殘骸回收永遠不會觸發。
        var lockToken = locks.Acquire("test", new Dictionary<string, string> { ["run_id"] = runId });

        PreparedEnvironment? prepared = null;
        int? daemonPid = null;

        try
        {
            // 單一場次的檢查必須在鎖之內。放在鎖之外的話，兩個並行的
            // run_test_cycle 會同時通過檢查、啟動兩個遊戲、互相覆寫狀態檔，
            // 其中一個的 PID 從此遺失成為孤兒。
            var active = sessions.Read();

            if (active.State == "running" && active.GamePid is { } pid && processes.IsAlive(pid))
            {
                throw new InvalidOperationException(
                    $"A test session is already running (run_id={active.RunId}). Call stop_test(confirm=true) first.");
            }

            // 上一場次的遊戲崩潰時 Stop() 沒被呼叫，它的連結還躺在使用者的
            // Mods 目錄裡——狀態檔即將被覆寫，這是最後的清理機會。
            environment.RemoveLinks(active.Links);

            // 使用者自己開著遊戲時不能動 Mods 目錄——連結會被鎖住，而且會干擾他的存檔。
            if (launcher.IsGameRunning(paths.Executable))
            {
                throw new InvalidOperationException("RimWorld is running. Close the game before starting a test.");
            }

            prepared = environment.Prepare(runId, mod, info.PackageId, paths, modSet.ActiveMods, token, seedFiles, fullscreen);

            diagnostics.Clear();
            gameState.Clear();

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
                SaveData = prepared.SaveData,
                ActiveMods = prepared.ActiveMods,
                SkippedLoadAfter = modSet.SkippedLoadAfter,
                Links = prepared.Links,
                PlayerLog = paths.PlayerLog,
                LogOffset = logOffset,
                BridgePort = locator.BridgePort(),
                Bridge = prepared.Bridge,
                Daemon = daemonState,
                DaemonPid = daemonPid,
                DaemonStartUtc = daemonStartUtc,
            };

            sessions.Write(session);

            using var game = launcher.Launch(paths, prepared.SaveData, quickTest, token);

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
            // Prepare 之後的失敗要把佈好的環境拆乾淨，否則使用者的 Mods 目錄
            // 會留下指向工作區的孤兒連結。（Prepare 內部的失敗由它自己回滾。）
            if (prepared is not null)
            {
                environment.RemoveLinks(prepared.Links);
                environment.DeleteBridgeToken();
            }

            if (daemonPid is { } owned)
            {
                processes.Terminate(owned);
            }

            throw;
        }
        finally
        {
            locks.Release("test", lockToken);
        }
    }

    /// <summary>進臨界區之前就把設定檔路徑驗證完，缺檔不該走到佈置環境才失敗。</summary>
    private static List<string> ResolveSeedConfig(IReadOnlyList<string>? seedConfig)
    {
        if (seedConfig is null || seedConfig.Count == 0)
        {
            return [];
        }

        var resolved = new List<string>(seedConfig.Count);

        foreach (var entry in seedConfig)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entry);

            var full = Path.GetFullPath(PathText.ExpandUser(entry));

            if (!File.Exists(full))
            {
                throw new FileNotFoundException($"seed_config file does not exist: {full}", full);
            }

            resolved.Add(full);
        }

        return resolved;
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
            launcher.WaitForExit(stoppedPid, TimeSpan.FromSeconds(20));
        }

        var daemonStopped = session.DaemonPid is { } daemonPid
            && processes.Terminate(daemonPid, session.DaemonStartUtc);

        var remaining = new List<TestLink>();

        foreach (var link in session.Links)
        {
            // Removed 以外的結果都要留在狀態裡：Failed 是還被佔用（下次可補清），
            // NotOurs 是路徑被別的東西佔著——使用者需要知道 Mods 目錄有殘留。
            if (environment.RemoveLink(link) != LinkRemoval.Removed)
            {
                remaining.Add(link);
            }
        }

        environment.DeleteBridgeToken();

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

        // 場次結束後就沒有遊戲了；留著舊狀態會讓 test_status 看起來像遊戲還在跑。
        gameState.Clear();
        return stopped;
    }

    public TestSession Status() => sessions.Read();

    private TestModSet ResolveModSet(ModInfo mod, IReadOnlyList<string>? companionMods)
    {
        // 同一個 packageId 可能同時出現在 Mods\ 與 Workshop，受測 Mod 也可能本來就住在 Mods\ 裡；
        // 以 packageId 去重，受測 Mod 一律以呼叫者指定的路徑為準。
        var available = new Dictionary<string, ModInfo>(StringComparer.Ordinal);

        foreach (var installed in catalog.BuiltinPacks().Concat(catalog.Installed()))
        {
            available.TryAdd(installed.PackageId, installed);
        }

        available[mod.PackageId] = mod;

        var selected = new List<ModInfo> { mod };

        foreach (var companion in companionMods ?? [])
        {
            if (!available.TryGetValue(companion.Trim().ToLowerInvariant(), out var match))
            {
                throw new KeyNotFoundException($"Companion mod not found: {companion}");
            }

            selected.Add(match);
        }

        var order = loadOrders.Resolve(selected, [.. available.Values]);

        if (order.Missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing required mod dependencies: {string.Join(", ", order.Missing)}");
        }

        return new TestModSet(order.Active, order.SkippedLoadAfter);
    }
}
