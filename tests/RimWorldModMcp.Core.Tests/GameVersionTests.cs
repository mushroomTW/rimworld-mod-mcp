using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Core.Tests;

public sealed class GameVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-version-" + Guid.NewGuid().ToString("n")[..12]);

    public GameVersionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("1.6.4871 rev590", "1.6")]
    [InlineData("1.5.4409 rev1114\n", "1.5")]
    [InlineData("1.6", "1.6")]
    public void ReadsMajorMinor(string content, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "Version.txt"), content);

        Assert.Equal(expected, GameVersion.ReadMajorMinor(_root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1")]
    public void MalformedContentIsNull(string content)
    {
        File.WriteAllText(Path.Combine(_root, "Version.txt"), content);

        Assert.Null(GameVersion.ReadMajorMinor(_root));
    }

    [Fact]
    public void MissingFileOrRootIsNull()
    {
        Assert.Null(GameVersion.ReadMajorMinor(_root));
        Assert.Null(GameVersion.ReadMajorMinor(null));
    }
}
