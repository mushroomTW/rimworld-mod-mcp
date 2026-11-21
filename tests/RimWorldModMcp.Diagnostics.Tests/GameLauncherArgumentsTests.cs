namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>啟動遊戲時的命令列參數。</summary>
public sealed class GameLauncherArgumentsTests
{
    private const string SaveData = "C:/tmp/run 1";

    [Test]
    public void DefaultLaunchHasNoHeadlessSwitches()
    {
        Assert.Equal(
            ["-savedatafolder=C:/tmp/run 1", "-quicktest"],
            GameLauncher.Arguments(SaveData, quickTest: true, headless: false));
    }

    /// <summary>無頭模式看不到當機對話框，所以要一併關掉，否則遊戲會卡在對話框上。</summary>
    [Test]
    public void HeadlessAlsoSuppressesTheCrashDialog()
    {
        Assert.Equal(
            ["-savedatafolder=C:/tmp/run 1", "-headless", "-NoCrashDialog"],
            GameLauncher.Arguments(SaveData, quickTest: false, headless: true));
    }
}
