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
    IReadOnlyList<string> SkippedLoadAfter,
    IReadOnlyList<string> LoadBeforeCoreConflicts,
    IReadOnlyList<string> OtherModFolders);

/// <summary>
/// 在隔離環境中啟動 RimWorld 測試開發中的 Mod。
///
/// <para>
/// 隔離靠三件事：-savedatafolder 指向暫存目錄（不碰使用者的存檔與設定）、
/// 自產的 ModsConfig.xml（只啟用這次要測的 Mod 與其相依）、
/// 以及 Mods 目錄下的臨時連結（不複製檔案，改動立即生效）。
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "DI orchestrator injecting required services")]
public sealed class TestCycleService(
    RimWorldLocator locator,
    IProcessHost processes,
    ModCatalog catalog,
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
        IReadOnlyList<string>? seedConfig = null)
    {
        var mod = PathText.ResolveDirectory(modPath);
        var seedFiles = ResolveSeedConfig(seedConfig);
        var paths = locator.Detect();

        if (paths.Executable is null || paths.ModsDir is null)
        {
            throw new DirectoryNotFoundException("RimWorld executable or Mods directory not found.");
        }

        var info = AboutXml.Parse(mod, "workspace", GameVersion.ReadMajorMinor(paths.InstallRoot))
            ?? throw new InvalidOperationException("The mod's About/About.xml is invalid or missing packageId.");

        var runId = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var modSet = ResolveModSet(info, companionMods);
        var harmonyWarning = HarmonyDependencyWarning(mod, modSet.ActiveMods);

        // 遊戲模組清單會把這些 Mod 標成順序錯誤；排序器無法同時滿足，只能回報。
        List<string> warnings = [
            .. modSet.LoadBeforeCoreConflicts.Select(id =>
                $"{id} declares loadBefore Ludeon.RimWorld, but its dependencies or load-after rules require it to load after Core, "
                + "so it was kept after Core. RimWorld will flag its position in the mod list."),
        ];

        if (harmonyWarning is not null)
        {
            warnings.Add(harmonyWarning);
        }

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

            // 遊戲自己重開過的話記錄的 PID 已經死了，必須連重開後的行程一起看——
            // 否則下面會拆掉那個仍在執行的遊戲正在用的連結。
            if (IsSessionGameAlive(active, paths.Executable))
            {
                throw new InvalidOperationException(
                    $"A test session is already running (run_id={active.RunId}). Call stop_test(confirm=true) first.");
            }

            // 上一場次的遊戲崩潰時 Stop() 沒被呼叫，它的連結還躺在使用者的
            // Mods 目錄裡——狀態檔即將被覆寫，這是最後的清理機會。
            //
            // 清不掉的必須帶進新場次（見下面的 Links）：丟掉結果的話，那個連結
            // 從此沒人追蹤，會永久留在使用者的 Mods 目錄，stop_test 也不會再補清。
            var orphanedLinks = TestEnvironmentPreparer.RemoveLinks(active.Links);

            // 使用者自己開著遊戲時不能動 Mods 目錄——連結會被鎖住，而且會干擾他的存檔。
            if (launcher.IsGameRunning(paths.Executable))
            {
                throw new InvalidOperationException("RimWorld is running. Close the game before starting a test.");
            }

            prepared = environment.Prepare(runId, mod, info.PackageId, paths, modSet.ActiveMods, token, new SeedConfigFiles(seedFiles, modSet.OtherModFolders));

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
                Warnings = warnings,
                // 上一場次清不掉的孤兒連結也納入追蹤，stop_test 才有機會補清並回報。
                Links = [.. prepared.Links, .. orphanedLinks],
                PlayerLog = paths.PlayerLog,
                LogOffset = logOffset,
                PreviousLogStamp = paths.PlayerLog is not null ? PlayerLogTailer.PreviousLogStamp(paths.PlayerLog) : null,
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
                TestEnvironmentPreparer.RemoveLinks(prepared.Links);
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

    /// <summary>停止測試場次並清理。與 Start 共用 test 鎖，避免拆掉啟動中的場次（F06）。</summary>
    public TestSession Stop(bool terminateGame)
    {
        // 與 Start 同一把跨程序鎖：Start 在「檢查→佈置→啟動」臨界區內持有它，
        // Stop 若不取鎖會在 starting 中間狀態刪除連結與目錄，造成狀態與環境不一致。
        // 鎖被佔用時拋 LockHeldException（可重試），而不是靜默破壞啟動中的場次。
        using var _ = locks.Hold("test", new Dictionary<string, string> { ["operation"] = "stop_test" });

        var session = sessions.Read();

        if (session.State is "idle" or "stopped")
        {
            return session;
        }

        var executable = locator.Detect().Executable;
        var (daemonStopped, gameStopped, survivors) = TerminateProcesses(session, terminateGame, executable);

        // 記錄的 PID 死了不代表遊戲結束了：遊戲自己重開後換了新 PID。
        SessionGame? aliveGame = null;
        if (session.GamePid is { } alivePid && processes.IsAlive(alivePid))
        {
            aliveGame = new SessionGame(alivePid, session.GameStartUtc);
        }
        else
        {
            var remainingGames = survivors ?? FindRestartedGames(session, executable);
            if (remainingGames.Count > 0)
            {
                aliveGame = remainingGames[0];
            }
        }
        var gameStillAlive = aliveGame is not null;

        // 遊戲還活著就不拆連結：它仍透過這些連結載入 Mod（例如切換語言後的重載）。
        // 連結留在 needs_cleanup 狀態裡，由之後的 stop_test(terminate_game=true) 清掉。
        var remaining = gameStillAlive ? [.. session.Links] : RemoveSessionLinks(session.Links);

        environment.DeleteBridgeToken();

        TryDeleteSaveData(session.SaveData, gameStillAlive);

        // 寫回前驗證 run_id：若期間有新場次寫入（例如另一個 Start 在我們等待鎖後搶先），
        // 不可用舊工作覆蓋新場次。
        var current = sessions.Read();

        if (session.RunId is not null && current.RunId is not null
            && !string.Equals(current.RunId, session.RunId, StringComparison.Ordinal)
            && current.State is "starting" or "running")
        {
            return current;
        }

        var saveDataRemaining = session.SaveData is not null && Directory.Exists(session.SaveData);

        // 遊戲仍存活或仍有殘留資源時，不可寫無動作的 stopped（會丟掉 PID 與連結追蹤，
        // 下一次 Stop(true) 因提前返回而永遠清不掉，F05）。
        if (gameStillAlive || remaining.Count > 0 || saveDataRemaining)
        {
            var pending = new TestSession
            {
                State = "needs_cleanup",
                RunId = session.RunId,
                StartedAt = session.StartedAt,
                Mod = session.Mod,
                SaveData = session.SaveData,
                ActiveMods = session.ActiveMods,
                SkippedLoadAfter = session.SkippedLoadAfter,
                Links = remaining,
                PlayerLog = session.PlayerLog,
                LogOffset = session.LogOffset,
                PreviousLogStamp = session.PreviousLogStamp,
                BridgePort = session.BridgePort,
                Bridge = session.Bridge,
                Daemon = session.Daemon,
                DaemonPid = session.DaemonPid,
                DaemonStartUtc = session.DaemonStartUtc,
                GamePid = aliveGame?.Pid,
                GameStartUtc = aliveGame?.StartUtc,
                PreviousRun = session.RunId,
                Terminated = new TerminationResult { Daemon = daemonStopped, Game = gameStopped },
            };

            sessions.Write(pending);
            return pending;
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
        var installedMods = catalog.Installed();
        var duplicates = DuplicateLocalCopies(mod.PackageId, mod.Path, installedMods);

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Another local copy of {mod.PackageId} is in the Mods folder ({string.Join(", ", duplicates)}). "
                + "RimWorld loads only one mod per packageId, so the test could run that copy instead of this one. "
                + "Move or rename the other copy, or test it in place by passing its path.");
        }

        // 同一個 packageId 可能同時出現在 Mods\ 與 Workshop，受測 Mod 也可能本來就住在 Mods\ 裡；
        // 以 packageId 去重，受測 Mod 一律以呼叫者指定的路徑為準。
        var available = new Dictionary<string, ModInfo>(StringComparer.Ordinal);

        foreach (var installed in catalog.BuiltinPacks().Concat(installedMods))
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

        var order = LoadOrderResolver.Resolve(selected, [.. available.Values]);

        if (order.Missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing required mod dependencies: {string.Join(", ", order.Missing)}");
        }

        // 受測 Mod 以外的 Mod 在測試場次裡沿用原本的資料夾名，seed_config 帶進來的
        // 它們的 ModSettings 不能跟著改名。
        var otherFolders = order.Active
            .Where(id => id != mod.PackageId)
            .Select(id => Path.GetFileName(Path.TrimEndingDirectorySeparator(available[id].Path)))
            .ToList();

        return new TestModSet(order.Active, order.SkippedLoadAfter, order.LoadBeforeCoreConflicts, otherFolders);
    }

    /// <summary>重開循環中終止一代可能正好生出下一代，重掃的上限輪數。</summary>
    private const int MaxRestartScans = 3;

    /// <summary>
    /// 終止遊戲與 daemon。<c>survivors</c> 是最後一輪掃描到、卻沒能終止的本場次遊戲行程；
    /// 沒掃描（未要求終止遊戲）或輪數用完時為 null，呼叫端要自己再掃一次。
    /// </summary>
    private (bool daemonStopped, bool gameStopped, IReadOnlyList<SessionGame>? survivors) TerminateProcesses(
        TestSession session, bool terminateGame, string? executable)
    {
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

        IReadOnlyList<SessionGame>? survivors = null;

        if (terminateGame)
        {
            // 遊戲自己重開過的話，記錄的 PID 早已結束，真正在跑的是命令列相同的新行程。
            // 放在上面之後再找：終止舊行程的瞬間它可能正好把新行程生出來；
            // 同理終止這一代時也可能再生出下一代，所以重掃到沒有可終止的為止。
            for (var pass = 0; pass < MaxRestartScans && survivors is null; pass++)
            {
                var found = FindRestartedGames(session, executable);
                var restarted = found.Where(game => processes.Terminate(game.Pid, game.StartUtc)).ToList();

                foreach (var game in restarted)
                {
                    launcher.WaitForExit(game.Pid, TimeSpan.FromSeconds(20));
                }

                gameStopped |= restarted.Count > 0;

                if (restarted.Count == 0)
                {
                    survivors = found;
                }
            }
        }

        var daemonStopped = session.DaemonPid is { } daemonPid
            && processes.Terminate(daemonPid, session.DaemonStartUtc);

        return (daemonStopped, gameStopped, survivors);
    }

    /// <summary>進行中（或待清理）的場次，其記錄的遊戲行程或重開後的行程是否還在執行。</summary>
    private bool IsSessionGameAlive(TestSession session, string? executable)
        => session.State is "running" or "needs_cleanup"
           && ((session.GamePid is { } pid && processes.IsAlive(pid))
               || FindRestartedGames(session, executable).Count > 0);

    /// <summary>本場次重開後的遊戲行程，見 <see cref="GameLauncher.FindSessionGames"/>。</summary>
    private IReadOnlyList<SessionGame> FindRestartedGames(TestSession session, string? executable)
        => executable is null || session.SaveData is null
            ? []
            : launcher.FindSessionGames(executable, session.SaveData);

    private static List<TestLink> RemoveSessionLinks(IEnumerable<TestLink> links)
    {
        var remaining = new List<TestLink>();

        foreach (var link in links)
        {
            // Removed 以外的結果都要留在狀態裡：Failed 是還被佔用（下次可補清），
            // NotOurs 是路徑被別的東西佔著——使用者需要知道 Mods 目錄有殘留。
            if (TestEnvironmentPreparer.RemoveLink(link) != LinkRemoval.Removed)
            {
                remaining.Add(link);
            }
        }

        return remaining;
    }

    private static void TryDeleteSaveData(string? saveData, bool gameStillAlive)
    {
        if (saveData is not null && Directory.Exists(saveData) && !gameStillAlive)
        {
            // 遊戲還活著就跳過刪除。Windows 上檔案鎖會擋下來，但 POSIX 允許
            // 刪除開啟中的檔案——terminate_game=false（預設）時在 Linux/macOS
            // 會把執行中遊戲的存檔整個刪掉。
            try
            {
                Directory.Delete(saveData, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 檔案仍被鎖住或是唯讀；下次啟動的清理會處理掉。漏接 UnauthorizedAccessException
                // 會讓 Stop 在連結已拆、daemon 已殺之後中斷，場次狀態停在 running。
            }
        }
    }

    /// <summary>
    /// Mods/ 底下與受測 Mod 同 packageId 的其他本機副本。RimWorld 對同 packageId 只載入一份
    /// 並記 Log.Error（ModLister.TryAddMod），測到的可能是舊副本。Workshop 副本不算——
    /// RimWorld 會替它加 _steam 後綴，兩者可並存；本工具自己的連結（上一場的殘骸）也不算。
    /// </summary>
    internal static IReadOnlyList<string> DuplicateLocalCopies(string packageId, string modPath, IEnumerable<ModInfo> installed)
    {
        var self = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modPath));

        return [.. installed
            .Where(m => m.Source == "local" && m.PackageId == packageId)
            .Where(m => !DirectoryLink.HasOwnedPrefix(m.Path))
            .Select(m => m.Path)
            .Where(p => !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)), self, PathText.Comparison))];
    }

    private const string HarmonyPackageId = "brrainz.harmony";
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>
    /// 受測 Mod 的組件參考了 0Harmony、自己卻沒帶 0Harmony.dll，而選集裡也沒有 Harmony Mod 時的警告。
    ///
    /// <para>
    /// Bridge 自帶 0Harmony.dll，所以這種 Mod 在測試場次裡照樣能跑——少宣告的
    /// brrainz.harmony 相依被掩蓋了，到玩家那邊才會壞。
    /// </para>
    /// </summary>
    internal static string? HarmonyDependencyWarning(string modPath, IReadOnlyCollection<string> activeMods)
    {
        if (activeMods.Contains(HarmonyPackageId, StringComparer.Ordinal))
        {
            return null;
        }

        var libraries = Directory
            .EnumerateFiles(modPath, "*.dll", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(path => !Path.GetRelativePath(modPath, path)
                .Split(PathSeparators)
                .SkipLast(1)
                .Any(segment => segment.Equals("Source", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (libraries.Any(path => Path.GetFileName(path).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var user = libraries.FirstOrDefault(ReferencesHarmony);

        return user is null
            ? null
            : $"{Path.GetFileName(user)} references 0Harmony, but the mod does not declare {HarmonyPackageId} in modDependencies. "
              + "It only works in this test session because the Bridge bundles Harmony; players without the Harmony mod will get load errors.";
    }

    private static bool ReferencesHarmony(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(stream);

            if (!pe.HasMetadata)
            {
                return false;
            }

            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);

            return metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .Any(name => name == "0Harmony");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // 原生 DLL、損壞或被鎖住的檔案：無從判斷，不警告。
            return false;
        }
    }
}
