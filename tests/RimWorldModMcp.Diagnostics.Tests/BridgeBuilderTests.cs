using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>預編譯 Bridge 的複製路徑：不需要 .NET SDK，也不需要遊戲。</summary>
public sealed class BridgeBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly string _prebuilt;
    private readonly string _modDirectory;

    public BridgeBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-bridge-" + Guid.NewGuid().ToString("n")[..12]);
        _source = Path.Combine(_root, "bridge");
        _prebuilt = Path.Combine(_source, "Prebuilt");
        _modDirectory = Path.Combine(_root, "cache", "bridge");

        Directory.CreateDirectory(Path.Combine(_source, "About"));
        Directory.CreateDirectory(_prebuilt);
        File.WriteAllText(Path.Combine(_source, "About", "About.xml"), "<ModMetaData />");
        File.WriteAllText(Path.Combine(_prebuilt, "RimWorldModMcp.Bridge.dll"), "bridge");
        File.WriteAllText(Path.Combine(_prebuilt, "0Harmony.dll"), "harmony");
        File.WriteAllText(Path.Combine(_prebuilt, "game-version.txt"), "1.6");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private BridgeBuild Ensure() => BridgeBuilder.EnsurePrebuilt(
        _source,
        _prebuilt,
        _modDirectory,
        Path.Combine(_modDirectory, "Assemblies"),
        Path.Combine(_modDirectory, ".build-stamp"));

    [Fact]
    public void CopiesAssembliesAndAboutIntoTheModDirectory()
    {
        var result = Ensure();

        Assert.True(result.Success, result.Error);
        Assert.Equal("prebuilt", result.Origin);
        Assert.True(result.Rebuilt);
        Assert.Equal(_modDirectory, result.ModDirectory);
        Assert.Equal("bridge", File.ReadAllText(Path.Combine(_modDirectory, "Assemblies", "RimWorldModMcp.Bridge.dll")));
        Assert.Equal("harmony", File.ReadAllText(Path.Combine(_modDirectory, "Assemblies", "0Harmony.dll")));
        Assert.True(File.Exists(Path.Combine(_modDirectory, "About", "About.xml")));

        // game-version.txt 是給 server 看的，不是 Mod 的一部分。
        Assert.False(File.Exists(Path.Combine(_modDirectory, "Assemblies", "game-version.txt")));
    }

    [Fact]
    public void UnchangedPrebuiltIsNotCopiedAgain()
    {
        Ensure();

        var again = Ensure();

        Assert.True(again.Success);
        Assert.False(again.Rebuilt);
        Assert.Equal("prebuilt", again.Origin);
    }

    /// <summary>工具更新後預編譯檔會變，快取裡的舊 DLL 必須被換掉。</summary>
    [Fact]
    public void UpdatedPrebuiltIsCopiedAgain()
    {
        Ensure();

        var dll = Path.Combine(_prebuilt, "RimWorldModMcp.Bridge.dll");
        File.WriteAllText(dll, "bridge-v2");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));

        var again = Ensure();

        Assert.True(again.Rebuilt);
        Assert.Equal("bridge-v2", File.ReadAllText(Path.Combine(_modDirectory, "Assemblies", "RimWorldModMcp.Bridge.dll")));
    }
}
