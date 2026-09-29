using System.Diagnostics;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>測試場次的一個遊戲行程與其啟動時間（取不到時為 null）。</summary>
public sealed record SessionGame(int Pid, DateTime? StartUtc);

/// <summary>
/// RimWorld 行程的生命週期：啟動、偵測、等待退出。
///
/// <para>
/// 從 <see cref="TestCycleService"/> 拆出來的第三塊——碰行程的操作
/// 全部在這裡，環境佈置歸 <see cref="TestEnvironmentPreparer"/>。
/// </para>
/// </summary>
public sealed class GameLauncher(RimWorldLocator locator, IProcessHost processes)
{
    public Process Launch(RimWorldPaths paths, string saveData, bool quickTest, string token)
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
            ?? throw new InvalidOperationException("Could not start RimWorld.");
    }

    /// <summary>是否有任何同名的 RimWorld 行程在執行（含使用者自己開的）。</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI instance service method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "DI instance service method")]
    public bool IsGameRunning(string executable)
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

    /// <summary>
    /// 屬於這個測試場次的 RimWorld 行程：同名、且命令列帶著本場次的 -savedatafolder。
    ///
    /// <para>
    /// 遊戲會自己重開（GenCommandLine.Restart，例如 HugsLib 在切換語言後呼叫），
    /// 新行程沿用相同參數但 PID 不同，只記 PID 的話 stop_test 就找不到它。
    /// 暫存目錄名含 run id，不會對到使用者自己開的遊戲。
    /// </para>
    /// </summary>
    public IReadOnlyList<SessionGame> FindSessionGames(string executable, string saveData)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var found = new List<SessionGame>();

        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (IsSessionCommandLine(ProcessCommandLine.Read(process.Id), saveData))
                    {
                        // 記下啟動時間，終止前比對以防 PID 在掃描與終止之間被重用。
                        found.Add(new SessionGame(process.Id, processes.StartTimeUtc(process.Id)));
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 列舉失敗就當作找不到；呼叫端仍會處理記錄下來的 PID。
        }

        return found;
    }

    /// <summary>
    /// 命令列是否帶著指向 <paramref name="saveData"/> 的 -savedatafolder。
    /// GenCommandLine.Restart 會把每個參數包上引號，所以只比對子字串。
    /// 目錄名後面必須緊接結尾、引號（Restart 的引號）、空白（Linux 的 /proc 以空白串接參數）
    /// 或路徑分隔符（允許尾斜線），避免 run id 前綴相同的目錄互相誤認。
    /// </summary>
    public static bool IsSessionCommandLine(string? commandLine, string saveData)
    {
        if (commandLine is null)
        {
            return false;
        }

        var argument = "-savedatafolder=" + Path.TrimEndingDirectorySeparator(saveData);
        var index = commandLine.IndexOf(argument, PathText.Comparison);

        while (index >= 0)
        {
            var end = index + argument.Length;

            if (end == commandLine.Length || commandLine[end] is '"' or ' ' or '\\' or '/')
            {
                return true;
            }

            index = commandLine.IndexOf(argument, end, PathText.Comparison);
        }

        return false;
    }

    /// <summary>等待行程真正退出。Kill 是非同步的，立刻去刪檔案會撞上檔案佔用。</summary>
    public void WaitForExit(int processId, TimeSpan timeout)
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
}
