using System.Diagnostics;

namespace RimWorldModMcp.Tests.Shared;

/// <summary>
/// 啟動一個會持續約 60 秒、命令列帶著指定標記的程序。
/// 以原始碼連結進 PlatformContracts 與 Diagnostics 兩個測試專案，平台差異只在這裡處理一次。
/// </summary>
internal static class SleeperProcess
{
    /// <summary>Windows 上的程序名是 cmd，其他平台是 sh。</summary>
    public static string Executable => OperatingSystem.IsWindows() ? "cmd.exe" : "sh";

    public static Process Start(string marker)
    {
        // `; true` 讓 sh 不能把 sleep 直接 exec 掉——exec 之後程序名與命令列都變成 sleep，標記就不見了。
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c timeout /t 60 /nobreak >nul & rem \"{marker}\"")
            : new ProcessStartInfo("sh", $"-c \"sleep 60; true\" \"{marker}\"");

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        return Process.Start(startInfo)!;
    }

    /// <summary>連同子程序（Windows 的 timeout.exe、sh 的 sleep）一起結束。</summary>
    public static void Stop(Process process)
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit(15_000);
    }
}
