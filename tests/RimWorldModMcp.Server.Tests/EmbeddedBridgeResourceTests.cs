using RimWorldModMcp.Server.Tools;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// Release 只發佈單一執行檔，run_test_cycle 全靠 Server 組件內嵌的 Bridge 原始碼就地建置。
/// 少了任何一個檔案，使用者端就建不出 Bridge。
/// </summary>
public sealed class EmbeddedBridgeResourceTests
{
    [Theory]
    [InlineData("bridge/About/About.xml")]
    [InlineData("bridge/Source/RimWorldModMcp.Bridge.csproj")]
    [InlineData("bridge/Source/BridgeMod.cs")]
    [InlineData("bridge/Source/DiagnosticPayload.cs")]
    [InlineData("bridge/Source/GameStatePayload.cs")]
    public void ServerAssemblyEmbedsTheBridgeFile(string name)
    {
        Assert.Contains(name, ResourceNames());
    }

    private static IEnumerable<string> ResourceNames() =>
        typeof(TestCycleTools).Assembly.GetManifestResourceNames().Select(Normalize);

    // RecursiveDir 在 Windows 上帶反斜線；執行期解出時同樣會正規化。
    private static string Normalize(string name) => name.Replace('\\', '/');
}
