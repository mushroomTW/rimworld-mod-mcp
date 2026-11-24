using System.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>啟動遊戲時的命令列參數。</summary>
public sealed class GameLauncherArgumentsTests
{
    private const string SaveData = "C:/tmp/run 1";

    [Test]
    public void DefaultLaunchHasNoHiddenWindowSwitches()
    {
        Assert.Equal(
            ["-savedatafolder=C:/tmp/run 1", "-quicktest"],
            GameLauncher.Arguments(SaveData, quickTest: true, noWindow: false));
    }

    /// <summary>隱藏視窗時沒有人會處理當機對話框，所以要一併關掉，否則遊戲會卡在對話框上。</summary>
    [Test]
    public void NoWindowAlsoSuppressesTheCrashDialog()
    {
        Assert.Equal(
            ["-savedatafolder=C:/tmp/run 1", "-NoWindow", "-NoCrashDialog"],
            GameLauncher.Arguments(SaveData, quickTest: false, noWindow: true));
    }

    /// <summary>遊戲的輸出不能混進 MCP 的 stdout，也不能因為管線沒有人讀而卡住或出錯。</summary>
    [Test]
    public void OutputIsDetachedWithoutBlockingTheProcess()
    {
        var startInfo = new ProcessStartInfo("dotnet", "--info") { UseShellExecute = false };

        using var process = GameLauncher.StartDetached(startInfo);

        Assert.True(startInfo.RedirectStandardInput && startInfo.RedirectStandardOutput && startInfo.RedirectStandardError);
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }
}
