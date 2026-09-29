using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Tests.Shared;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>
/// 認出屬於測試場次的遊戲行程。RimWorld 會自己重開（GenCommandLine.Restart），
/// 新行程換了 PID，stop_test 只能靠命令列上的 -savedatafolder 找到它。
/// </summary>
public sealed class SessionGameMatchingTests
{
    private static readonly string SaveData = Path.Combine(Path.GetTempPath(), "RimWorldModMcp", "test-savedata", "1790611144-ea19e12d");

    /// <summary>GameLauncher 啟動時的形式，與 GenCommandLine.Restart 把每個參數加上引號後的形式。</summary>
    [Theory]
    [InlineData("\"{exe}\" -savedatafolder={save} -quicktest")]
    [InlineData("{exe} \"-savedatafolder={save}\" \"-quicktest\"")]
    [InlineData("{exe} -savedatafolder={save}")]
    public void LaunchAndRestartCommandLinesMatch(string template)
    {
        var commandLine = template.Replace("{exe}", "RimWorldWin64.exe").Replace("{save}", SaveData);

        Assert.True(GameLauncher.IsSessionCommandLine(commandLine, SaveData));
    }

    /// <summary>使用者自己開的遊戲、別的場次（run id 只差後綴）都不能被當成本場次而被終止。</summary>
    [Theory]
    [InlineData("RimWorldWin64.exe")]
    [InlineData("RimWorldWin64.exe -savedatafolder={save}0 -quicktest")]
    [InlineData("RimWorldWin64.exe -savedatafolder={save}-other")]
    [InlineData(null)]
    public void OtherGamesDoNotMatch(string? template)
    {
        var commandLine = template?.Replace("{save}", SaveData);

        Assert.False(GameLauncher.IsSessionCommandLine(commandLine, SaveData));
    }

    /// <summary>用真的行程走一遍：同名行程裡只挑出命令列帶本場次目錄的那一個。</summary>
    [Fact]
    public void FindSessionGamesPicksTheProcessCarryingTheSaveDataFolder()
    {
        var saveData = Path.Combine(Path.GetTempPath(), "rwmm-session " + Guid.NewGuid().ToString("N"));
        var launcher = new GameLauncher(new RimWorldLocator(), new ProcessHost());

        using var session = SleeperProcess.Start($"-savedatafolder={saveData}");
        using var other = SleeperProcess.Start($"-savedatafolder={saveData}-other");

        try
        {
            Assert.Equal([session.Id], launcher.FindSessionGames(SleeperProcess.Executable, saveData).Select(game => game.Pid));
        }
        finally
        {
            SleeperProcess.Stop(session);
            SleeperProcess.Stop(other);
        }
    }
}
