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
    [TestCase("1.6.4871 rev590", "1.6")]
    [TestCase("1.5.4409 rev1114\n", "1.5")]
    [TestCase("1.6", "1.6")]
    public void ReadsMajorMinor(string content, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "Version.txt"), content);

        Assert.Equal(expected, GameVersion.ReadMajorMinor(_root));
    }
    [TestCase("1.7.4976 rev1043\n", "1.7.4976 rev1043")]
    [TestCase("1.7", "1.7")]
    public void ReadsFullVersion(string content, string expected)
    {
        File.WriteAllText(Path.Combine(_root, "Version.txt"), content);

        Assert.Equal(expected, GameVersion.ReadFull(_root));
    }
    [TestCase("")]
    [TestCase("garbage")]
    [TestCase("1")]
    public void MalformedContentIsNull(string content)
    {
        File.WriteAllText(Path.Combine(_root, "Version.txt"), content);

        Assert.Null(GameVersion.ReadMajorMinor(_root));
    }

    [Test]
    public void MissingFileOrRootIsNull()
    {
        Assert.Null(GameVersion.ReadMajorMinor(_root));
        Assert.Null(GameVersion.ReadMajorMinor(null));
    }
}
