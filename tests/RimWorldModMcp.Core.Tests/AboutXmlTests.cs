using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Core.Tests;

public sealed class AboutXmlTests : IDisposable
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "rwmm-about-" + Guid.NewGuid().ToString("n")[..12]);

    public AboutXmlTests()
    {
        Directory.CreateDirectory(Path.Combine(_mod, "About"));
        File.WriteAllText(Path.Combine(_mod, "About", "About.xml"), """
            <ModMetaData>
              <packageId>me.mod</packageId>
              <modDependencies>
                <li><packageId>base.dep</packageId></li>
              </modDependencies>
              <modDependenciesByVersion>
                <v1.6>
                  <li><packageId>Brrainz.Harmony</packageId></li>
                </v1.6>
                <v1.5>
                  <li><packageId>old.dep</packageId></li>
                </v1.5>
              </modDependenciesByVersion>
              <loadAfterByVersion>
                <v1.6>
                  <li>after.one</li>
                </v1.6>
              </loadAfterByVersion>
            </ModMetaData>
            """);
    }

    public void Dispose() => Directory.Delete(_mod, recursive: true);

    /// <summary>
    /// RimWorld 的 InitVersionedData：*ByVersion 裡有目前版本的條目時，**取代**基本清單。
    /// 忽略它的話，只在 ByVersion 宣告的硬相依不會被帶進測試場次。
    /// </summary>
    [Fact]
    public void ByVersionEntryForTheGameVersionReplacesTheBaseList()
    {
        var info = AboutXml.Parse(_mod, "local", "1.6")!;

        Assert.Equal(["brrainz.harmony"], info.Dependencies);
        Assert.Equal(["after.one"], info.LoadAfter);
    }

    [Fact]
    public void BaseListIsUsedWhenNoEntryMatchesTheVersion()
    {
        var info = AboutXml.Parse(_mod, "local", "1.4")!;

        Assert.Equal(["base.dep"], info.Dependencies);
        Assert.Empty(info.LoadAfter);
    }
}
