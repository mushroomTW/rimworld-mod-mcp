using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core.Tests;

public sealed class CheckpointServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _modDirectory;
    private readonly ValidatedModPath _mod;
    private readonly WorkspaceRegistry _workspaces;
    private readonly CheckpointService _checkpoints;

    public CheckpointServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-ckpt-" + Guid.NewGuid().ToString("n")[..12]);

        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _workspaces = new WorkspaceRegistry(store);

        var workspace = Path.Combine(_root, "Workspace");
        _modDirectory = Path.Combine(workspace, "MyMod");
        Directory.CreateDirectory(_modDirectory);
        _workspaces.Configure(workspace);

        // ValidatedModPath 只能由 WorkspaceRegistry 產生——測試也走同一條路。
        _mod = _workspaces.AllowedMod(_modDirectory);

        _checkpoints = new CheckpointService(store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// checkpoint_id 來自 MCP 參數直傳並拼進路徑。不驗證格式的話，
    /// <c>../../..</c> 可以把任意目錄整棵複製進工作區——任意檔案讀取原語。
    /// </summary>
    [Theory]
    [InlineData("../../../secrets")]
    [InlineData("..\\..\\evil")]
    [InlineData("not-a-checkpoint-id")]
    [InlineData("")]
    public void RestoreRejectsMalformedCheckpointIds(string checkpointId)
    {
        Assert.Throws<ArgumentException>(() => _checkpoints.Restore(_mod, checkpointId));
    }

    [Fact]
    public void CreateThenRestoreRoundTripsContent()
    {
        var file = Path.Combine(_modDirectory, "Defs", "Things.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "original");

        var checkpoint = _checkpoints.Create(_mod);

        File.WriteAllText(file, "modified");

        var (restored, safety) = _checkpoints.Restore(_mod, checkpoint.Id);

        Assert.Equal("original", File.ReadAllText(file));
        Assert.Equal(_mod.Value, restored);

        // 還原前會自動建立安全快照，讓誤操作可以回頭。
        Assert.Contains(_checkpoints.List(_mod), c => c.Id == safety);
    }

    /// <summary>大小寫不同的同一條路徑必須落在同一個快照根。</summary>
    [Fact]
    public void CheckpointsSurvivePathCasingDifferences()
    {
        if (OperatingSystem.IsLinux())
        {
            // Linux 的檔案系統區分大小寫，大小寫不同就是不同路徑，不適用此契約。
            return;
        }

        File.WriteAllText(Path.Combine(_modDirectory, "About.xml"), "<ModMetaData />");

        var checkpoint = _checkpoints.Create(_mod);

        var upper = char.IsLower(_modDirectory[0])
            ? char.ToUpperInvariant(_modDirectory[0]) + _modDirectory[1..]
            : char.ToLowerInvariant(_modDirectory[0]) + _modDirectory[1..];

        Assert.Contains(_checkpoints.List(_workspaces.AllowedMod(upper)), c => c.Id == checkpoint.Id);
    }
}
