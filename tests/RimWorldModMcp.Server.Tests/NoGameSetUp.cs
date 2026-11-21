using NUnit.Framework;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// 這個組件的測試自己往索引塞資料：不能讓自動重建偵測到這台機器上真的 RimWorld、把資料洗掉。
/// 整個組件跑之前把遊戲路徑指向一個空目錄（等同 CI 上沒裝遊戲），跑完再還原。
/// 放在組件層級而不是各 fixture：平行的 fixture 各自設定又各自還原，會讓別人跑到一半退回真的遊戲。
/// </summary>
[SetUpFixture]
public sealed class NoGameSetUp
{
    private string? _previous;
    private string? _noGame;

    [OneTimeSetUp]
    public void PointAtAnEmptyGameDirectory()
    {
        var name = EnvironmentVariables.Name("GAME_PATH");
        _previous = Environment.GetEnvironmentVariable(name);
        _noGame = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rwmm-no-game-" + Guid.NewGuid().ToString("n")[..12])).FullName;
        Environment.SetEnvironmentVariable(name, _noGame);
    }

    [OneTimeTearDown]
    public void Restore()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariables.Name("GAME_PATH"), _previous);

        if (_noGame is not null && Directory.Exists(_noGame))
        {
            Directory.Delete(_noGame);
        }
    }
}
