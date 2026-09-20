using System.Diagnostics;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 確保診斷 daemon 正在執行。
///
/// <para>
/// 不做「先探測埠再啟動」。那個作法在探測與啟動之間有空窗，兩個並行的測試
/// 場次會各自判定埠是空的而各起一個 daemon，其中一個 bind 失敗後靜默死掉。
/// 這裡讓作業系統的 bind 當唯一的仲裁者：直接啟動子行程，由它自己去綁；
/// 綁成功才寫自述檔。父行程只等自述檔出現或子行程結束。
/// </para>
/// </summary>
public sealed class DaemonBootstrapper(
    RimWorldLocator locator,
    IProcessHost processes,
    DaemonRecordStore records)
{
    /// <summary>子行程因為埠已被佔用而結束時的離開碼。</summary>
    public const int PortInUseExitCode = 3;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);

    /// <summary>確保 daemon 可用，回傳它的狀態與（若由本次啟動）行程 ID。</summary>
    public (DaemonState State, int? OwnedPid) Ensure()
    {
        var port = locator.BridgePort();
        var existing = records.Read();

        // 已經有一個活著的、綁在同一個埠上的 daemon：直接沿用。
        // OwnedPid 回傳 null——不是我們啟動的，停止測試時不可以殺它。
        //
        // 啟動時間也要對得上：只比 PID 的話，作業系統重用 PID 時會把一個
        // 不相干的程序認作自家 daemon，遊戲的診斷從此石沉大海而狀態顯示正常。
        if (existing is not null
            && existing.Port == port
            && processes.IsAlive(existing.Pid)
            && StartTimeMatches(existing))
        {
            return (new DaemonState
            {
                State = "reused",
                Port = port,
                OwnerPid = existing.Pid,
            }, null);
        }

        using var process = Spawn();

        if (process is null)
        {
            return (Unavailable(port, "Could not start the diagnostics daemon subprocess."), null);
        }

        var deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                // 子行程綁不到埠就會用這個離開碼退出，代表埠被別人佔著。
                var reason = process.ExitCode == PortInUseExitCode
                    ? $"Port {port} is held by a non-service process; the Bridge cannot report diagnostics and this run falls back to Player.log only."
                      + $" Set RIMWORLD_MOD_MCP_BRIDGE_PORT to use another port."
                    : $"Diagnostics daemon failed to start (exit code {process.ExitCode}).";

                return (Unavailable(port, reason), null);
            }

            var record = records.Read();

            // 自述檔在 bind 成功之後才寫，所以看到它就代表埠真的是我們的。
            if (record is not null && record.Pid == process.Id && record.Port == port)
            {
                return (new DaemonState
                {
                    State = "started",
                    Port = port,
                    OwnerPid = process.Id,
                }, process.Id);
            }

            Thread.Sleep(100);
        }

        // 逾時的子行程必須殺掉：它可能在第 11 秒才 bind 成功並佔住埠，
        // 而此時已經沒有任何人記得它的 PID，stop_test 永遠不會清它。
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 行程剛好自己退出了，或無權終止。
        }

        return (Unavailable(port, $"Diagnostics daemon did not report ready within {StartupTimeout.TotalSeconds} seconds."), null);
    }

    /// <summary>自述檔記錄的啟動時間與實際程序是否吻合（容忍一秒的檔案往返精度差）。</summary>
    private bool StartTimeMatches(DaemonRecord record)
    {
        var recorded = record.StartTimeUtc;
        var actual = processes.StartTimeUtc(record.Pid);

        if (recorded is null || actual is null)
        {
            // 任一邊拿不到就退回「只比 PID」的既有行為。
            return true;
        }

        return Math.Abs((recorded.Value - actual.Value).TotalSeconds) <= 1;
    }

    private static DaemonState Unavailable(int port, string reason) => new()
    {
        State = "unavailable",
        Port = port,
        Reason = reason,
    };

    /// <summary>
    /// 啟動自己的另一個實例並帶上 daemon 子指令。
    /// 要同時處理兩種宿主：以 dotnet tool 安裝時 ProcessPath 是原生的 apphost，
    /// 用 dotnet run 時則是 dotnet 本體，後者需要額外把組件路徑當第一個參數。
    /// </summary>
    private static Process? Spawn()
    {
        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // 不重導：沒有人讀這兩條管線。daemon 輸出量小，但緩衝區一旦填滿
            // 就會永久阻塞——與遊戲行程同一個教訓。
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RimWorldModMcp.Server.dll"));
        }

        startInfo.ArgumentList.Add("daemon");

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
