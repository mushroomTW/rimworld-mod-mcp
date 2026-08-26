using System.Diagnostics;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 確保診斷 daemon 正在執行。
///
/// <para>
/// <b>不做「先探測埠再啟動」</b>。那個作法在探測與啟動之間有空窗，兩個並行的測試
/// 場次會各自判定埠是空的而各起一個 daemon，其中一個 bind 失敗後靜默死掉。
/// 這裡讓作業系統的 bind 當唯一的仲裁者：直接啟動子行程，由它自己去綁；
/// 綁成功才寫自述檔。父行程只等自述檔出現或子行程結束。
/// </para>
/// </summary>
public sealed class DaemonBootstrapper(
    IRimWorldLocator locator,
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
        if (existing is not null && existing.Port == port && processes.IsAlive(existing.Pid))
        {
            return (new DaemonState
            {
                State = "reused",
                Port = port,
                OwnerPid = existing.Pid,
            }, null);
        }

        var process = Spawn();

        if (process is null)
        {
            return (Unavailable(port, "無法啟動診斷 daemon 子行程。"), null);
        }

        var deadline = DateTime.UtcNow + StartupTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                // 子行程綁不到埠就會用這個離開碼退出，代表埠被別人佔著。
                var reason = process.ExitCode == PortInUseExitCode
                    ? $"連接埠 {port} 被非本服務的程序占用，Bridge 無法回報診斷，本輪僅由 Player.log 提供。"
                      + $"可設定 RIMWORLD_MOD_MCP_BRIDGE_PORT 改用其他埠。"
                    : $"診斷 daemon 啟動失敗（離開碼 {process.ExitCode}）。";

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

        return (Unavailable(port, $"診斷 daemon 在 {StartupTimeout.TotalSeconds} 秒內沒有回報就緒。"), null);
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
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
