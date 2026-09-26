namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>
/// Release 只發佈單一執行檔，旁邊沒有 bridge/ 目錄；run_test_cycle 改用內嵌在執行檔裡的 Bridge，
/// 需要時解到快取目錄。解錯位置或沒解出來，場次就只剩 Player.log 診斷。
/// </summary>
public sealed class EmbeddedBridgeTests : IDisposable
{
    private readonly string _target = Path.Combine(Path.GetTempPath(), "rwmm-embedded-" + Guid.NewGuid().ToString("n")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_target))
        {
            Directory.Delete(_target, recursive: true);
        }
    }

    [Fact]
    public void ExtractsEveryEmbeddedFileUnderItsRelativePath()
    {
        var directory = EmbeddedBridge.Extract(typeof(EmbeddedBridgeTests).Assembly, _target);

        Assert.Equal(_target, directory);
        Assert.Contains("fixture.bridge", File.ReadAllText(Path.Combine(_target, "About", "About.xml")), StringComparison.Ordinal);
        Assert.Equal("1.6", File.ReadAllText(Path.Combine(_target, "Prebuilt", "game-version.txt")).Trim());
    }

    /// <summary>同一個執行檔重複呼叫沿用已解出的檔案；換了執行檔（戳記不同）才整個重解，不留舊版殘檔。</summary>
    [Fact]
    public void OnlyReExtractsWhenTheExecutableChanges()
    {
        var assembly = typeof(EmbeddedBridgeTests).Assembly;
        EmbeddedBridge.Extract(assembly, _target);

        var leftover = Path.Combine(_target, "Source", "Removed.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "// from an older version");

        EmbeddedBridge.Extract(assembly, _target);
        Assert.True(File.Exists(leftover), "戳記相同時不該重解");

        File.WriteAllText(Path.Combine(_target, EmbeddedBridge.StampFile), "older-build");
        EmbeddedBridge.Extract(assembly, _target);

        Assert.False(File.Exists(leftover), "換版本重解時必須清掉舊檔");
        Assert.True(File.Exists(Path.Combine(_target, "About", "About.xml")));
    }

    [Fact]
    public void AssemblyWithoutEmbeddedBridgeReturnsNull()
    {
        Assert.Null(EmbeddedBridge.Extract(typeof(object).Assembly, _target));
        Assert.False(Directory.Exists(_target));
    }
}
