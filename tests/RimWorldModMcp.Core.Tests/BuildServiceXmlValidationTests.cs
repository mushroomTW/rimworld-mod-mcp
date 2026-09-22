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
