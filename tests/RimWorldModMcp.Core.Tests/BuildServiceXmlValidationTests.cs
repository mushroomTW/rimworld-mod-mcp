using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Core.Tests;

/// <summary>
/// F07：XML-only Mod 的有效性檢查必須檢查 Def XML。
/// 舊實作只驗 About.xml，沒有 C# 專案就宣告成功，未閉合的 Defs 也放行。
/// </summary>
public sealed class BuildServiceXmlValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-build-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly BuildService _service = new(new RimWorldLocator());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void BrokenDefXmlFailsTheBuild()
    {
        var mod = NewMod();
        Directory.CreateDirectory(Path.Combine(mod, "Defs"));
        File.WriteAllText(Path.Combine(mod, "Defs", "Broken.xml"), "<Defs><ThingDef>");

        var error = Assert.Throws<InvalidOperationException>(() => _service.Build(mod));

        Assert.Contains("Defs/Broken.xml", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrokenPatchXmlFailsTheBuild()
    {
        var mod = NewMod();
        Directory.CreateDirectory(Path.Combine(mod, "Patches"));
        File.WriteAllText(Path.Combine(mod, "Patches", "Broken.xml"), "<Patch><Operation>");

        Assert.Throws<InvalidOperationException>(() => _service.Build(mod));
    }

    /// <summary>版本資料夾（1.6/Defs、Common/Patches）同樣會被遊戲載入，壞了一樣要擋。</summary>
    [Theory]
    [InlineData("1.6", "Defs")]
    [InlineData("Common", "Patches")]
    public void BrokenXmlInAVersionFolderFailsTheBuild(string folder, string kind)
    {
        var mod = NewMod();
        Directory.CreateDirectory(Path.Combine(mod, folder, kind));
        File.WriteAllText(Path.Combine(mod, folder, kind, "Broken.xml"), "<Defs><ThingDef>");

        var error = Assert.Throws<InvalidOperationException>(() => _service.Build(mod));

        Assert.Contains($"{folder}/{kind}/Broken.xml", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// C# Mod 的 Defs 也要驗：舊實作只在 XML-only 路徑驗，C# Mod 的 Defs 壞了照樣回 success。
    /// 驗證在 dotnet build 之前，所以這個測試不需要 SDK 建置成功。
    /// </summary>
    [Fact]
    public void BrokenDefXmlFailsACSharpModBeforeBuilding()
    {
        var mod = NewMod();
        Directory.CreateDirectory(Path.Combine(mod, "Source"));
        File.WriteAllText(Path.Combine(mod, "Source", "Mod.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(mod, "Defs"));
        File.WriteAllText(Path.Combine(mod, "Defs", "Broken.xml"), "<Defs><ThingDef>");

        var error = Assert.Throws<InvalidOperationException>(() => _service.Build(mod));

        Assert.Contains("Defs/Broken.xml", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidXmlOnlyModStillPasses()
    {
        var mod = NewMod();
        Directory.CreateDirectory(Path.Combine(mod, "Defs"));
        File.WriteAllText(
            Path.Combine(mod, "Defs", "Things.xml"),
            "<Defs><ThingDef><defName>Good</defName></ThingDef></Defs>");

        var result = _service.Build(mod);

        Assert.Equal("xml_only", result.Kind);
        Assert.True(result.Success);
    }

    private string NewMod()
    {
        var mod = Path.Combine(_root, Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(Path.Combine(mod, "About"));
        File.WriteAllText(
            Path.Combine(mod, "About", "About.xml"),
            "<ModMetaData><packageId>author.testmod</packageId><name>Test</name></ModMetaData>");
        return mod;
    }
}
