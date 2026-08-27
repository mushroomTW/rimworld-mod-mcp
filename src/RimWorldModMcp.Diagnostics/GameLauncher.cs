using System.Diagnostics;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// RimWorld 行程的生命週期：啟動、偵測、等待退出。
///
/// <para>
/// 從 <see cref="TestCycleService"/> 拆出來的第三塊——碰行程的操作
/// 全部在這裡，環境佈置歸 <see cref="TestEnvironmentPreparer"/>。
/// </para>
/// </summary>
public sealed class GameLauncher(IRimWorldLocator locator, IProcessHost processes)
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
            ?? throw new InvalidOperationException("無法啟動 RimWorld。");
    }

    /// <summary>是否有任何同名的 RimWorld 行程在執行（含使用者自己開的）。</summary>
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
