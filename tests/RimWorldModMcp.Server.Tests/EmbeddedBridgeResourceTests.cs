using System.Text;
using RimWorldModMcp.Server.Tools;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// Release 只發佈單一執行檔，run_test_cycle 全靠 Server 組件內嵌的 Bridge。
/// 少了任何一個檔案，使用者端就會退回就地建置（需要 SDK）或完全沒有 Bridge。
/// </summary>
public sealed class EmbeddedBridgeResourceTests
{
    [Theory]
    [InlineData("bridge/About/About.xml")]
    [InlineData("bridge/Source/RimWorldModMcp.Bridge.csproj")]
    [InlineData("bridge/Prebuilt/RimWorldModMcp.Bridge.dll")]
    [InlineData("bridge/Prebuilt/0Harmony.dll")]
    [InlineData("bridge/Prebuilt/game-version.txt")]
    public void ServerAssemblyEmbedsTheBridgeFile(string name)
    {
        Assert.Contains(name, ResourceNames());
    }

    /// <summary>RimWorld 跑在 Mono 上，只載得了 net472；內嵌的預編譯 Bridge 編成其他目標框架等於沒有。</summary>
    [Fact]
    public void EmbeddedPrebuiltBridgeTargetsNet472()
    {
        var assembly = typeof(TestCycleTools).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => Normalize(resource) == "bridge/Prebuilt/RimWorldModMcp.Bridge.dll");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        Assert.Contains(".NETFramework,Version=v4.7.2", Encoding.ASCII.GetString(buffer.ToArray()), StringComparison.Ordinal);
    }

    private static IEnumerable<string> ResourceNames() =>
        typeof(TestCycleTools).Assembly.GetManifestResourceNames().Select(Normalize);

    // RecursiveDir 在 Windows 上帶反斜線；執行期解出時同樣會正規化。
    private static string Normalize(string name) => name.Replace('\\', '/');
}
