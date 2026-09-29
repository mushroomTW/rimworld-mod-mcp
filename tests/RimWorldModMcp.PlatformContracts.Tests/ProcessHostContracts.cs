using System.Diagnostics;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Tests.Shared;

namespace RimWorldModMcp.PlatformContracts.Tests;

/// <summary>
/// 程序存活與終止的平台契約。
/// 這兩條決定了鎖的殘骸接管是否安全，判錯方向會讓併發保護整個失效。
/// </summary>
public sealed class ProcessHostContracts
{
    private readonly ProcessHost _processes = new();

    /// <summary>契約：活著→true、已結束→false、PID 非正數→false。</summary>
    [Fact]
    public void IsAliveDistinguishesLiveFromExited()
    {
        Assert.True(_processes.IsAlive(Environment.ProcessId));

        using (var exited = StartShortLivedProcess())
        {
            exited.WaitForExit();
            Assert.False(_processes.IsAlive(exited.Id));
        }

        Assert.False(_processes.IsAlive(0));
        Assert.False(_processes.IsAlive(-1));
    }

    /// <summary>
    /// 契約：不確定時必須回傳 true。
    ///
    /// <para>
    /// 直接製造「權限不足」的情境在測試裡不可靠（要看使用者身分），
    /// 所以改為驗證系統程序不會被誤判成死亡——那正是權限不足最常出現的地方。
    /// PID 1（Unix init）／PID 4（Windows System）都不是我們能開啟的程序。
    /// </para>
    /// </summary>
    [Fact]
    public void IsAliveTreatsInaccessibleProcessesAsAlive()
    {
        var systemPid = OperatingSystem.IsWindows() ? 4 : 1;

        // 這個程序一定存在但通常無法完整開啟。實作必須回報「活著」而不是因為
        // 拿不到資訊就判定死亡——判死會讓殘骸接管誤搶別人持有的鎖。
        Assert.True(_processes.IsAlive(systemPid));
    }

    /// <summary>契約：能終止自己啟動的程序，且對無效 PID 回傳 false。</summary>
    [Fact]
    public void TerminateStopsAProcessWeStarted()
    {
        using var process = StartLongRunningProcess();

        Assert.True(_processes.IsAlive(process.Id));
        Assert.True(_processes.Terminate(process.Id));

        Assert.True(process.WaitForExit(milliseconds: 15_000));
        Assert.False(_processes.IsAlive(process.Id));

        Assert.False(_processes.Terminate(-1));
    }

    /// <summary>
    /// 契約：啟動時間可用來配合 PID 辨識同一個程序。
    /// 這是 Python 版沒有的防護——單看 PID 會在 PID 重用時誤判。
    /// </summary>
    [Fact]
    public void StartTimeDistinguishesRecycledPids()
    {
        var mine = _processes.StartTimeUtc(Environment.ProcessId);

        Assert.NotNull(mine);
        Assert.True(mine!.Value <= DateTime.UtcNow.AddSeconds(1));
        Assert.Null(_processes.StartTimeUtc(-1));
    }

    /// <summary>
    /// 契約：讀得到自己啟動的程序的命令列，對無效 PID 回傳 null。
    /// stop_test 靠它認出遊戲自己重開後的新行程（命令列帶著同一個 -savedatafolder）。
    /// </summary>
    [Fact]
    public void CommandLineOfAProcessWeStartedIsReadable()
    {
        var marker = "-savedatafolder=" + Path.Combine(Path.GetTempPath(), "cmdline contract " + Guid.NewGuid().ToString("N"));

        using var process = SleeperProcess.Start(marker);

        try
        {
            Assert.Contains(marker, ProcessCommandLine.Read(process.Id));
        }
        finally
        {
            SleeperProcess.Stop(process);
        }

        Assert.Null(ProcessCommandLine.Read(-1));
    }

    private static Process StartShortLivedProcess() => Start("--version");

    private static Process StartLongRunningProcess()
    {
        // 用 dotnet 本身當作測試目標，三個平台都一定存在。
        // `dotnet tool list --global` 會等待輸出，這裡改用會停住的互動式指令並不可靠，
        // 因此改成執行一個明確會持續的 shell sleep。
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 60 /nobreak")
            : new ProcessStartInfo("sh", "-c \"sleep 60\"");

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        return Process.Start(startInfo)!;
    }

    private static Process Start(string arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        return Process.Start(startInfo)!;
    }
}
