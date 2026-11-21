using RimWorldModMcp.Indexing.Semantics;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>find_def_usages 的 C# 使用處比對與 Mod XML 即時掃描。</summary>
public sealed class DefReferenceAnalyzerTests : IDisposable
{
    private readonly string _mod = Path.Combine(Path.GetTempPath(), "rwmm-def-refs-" + Guid.NewGuid().ToString("n")[..12]);

    public void Dispose()
    {
        if (Directory.Exists(_mod))
        {
            Directory.Delete(_mod, recursive: true);
        }
    }

    [TestCase("thing.def == ThingDefOf.Steel;")]
    [TestCase("var x = MyModDefOf.Steel.label;")]
    [TestCase("DefOf.Steel")]
    public void RecognisesDefOfUsages(string line)
    {
        Assert.True(DefReferenceAnalyzer.IsDefOfUsage(line, "Steel"));
    }

    /// <summary>名字只是前綴、大小寫不同、或根本不是 DefOf 類別，都不算。</summary>
    [TestCase("ThingDefOf.SteelSlag")]
    [TestCase("ThingDefOf.steel")]
    [TestCase("Things.Steel")]
    [TestCase("ThingDefOfX.Steel")]
    public void IgnoresLookalikes(string line)
    {
        Assert.False(DefReferenceAnalyzer.IsDefOfUsage(line, "Steel"));
    }

    [Test]
    public void FindsReferencesInTheModsDefXmlOnly()
    {
        Write("1.7/Defs/Recipes.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <Defs>
              <RecipeDef>
                <defName>MakeWidget</defName>
                <ingredients>
                  <li><filter><thingDefs><li>Steel</li></thingDefs></filter></li>
                </ingredients>
                <costList><Steel>10</Steel></costList>
              </RecipeDef>
            </Defs>
            """);

        // 不是 <Defs> 的檔案（Patches、語言檔）即使提到也不算。
        Write("Patches/Steel.xml", "<Patch><Operation><xpath>Defs/ThingDef[defName=\"Steel\"]</xpath></Operation></Patch>");
        Write("Languages/English/Keyed/Steel.xml", "<LanguageData><Steel>Steel</Steel></LanguageData>");

        // RimWorld 只從 Defs/ 讀定義：放在別處的 <Defs> 檔不會被載入，也不算。
        Write("Unused/Steel.xml", "<Defs><RecipeDef><costList><Steel>1</Steel></costList></RecipeDef></Defs>");

        var found = DefReferenceAnalyzer.FindInModXml(_mod, "Steel", 100);

        Assert.Equal(2, found.Count);
        Assert.True(found.All(f => f.FilePath == "1.7/Defs/Recipes.xml"));
        Assert.Equal([6, 8], found.Select(f => f.Line).Order());
    }

    [Test]
    public void StopsAtTheLimit()
    {
        Write("Defs/A.xml", "<Defs><RecipeDef><costList><Steel>1</Steel></costList></RecipeDef></Defs>");
        Write("Defs/B.xml", "<Defs><RecipeDef><costList><Steel>1</Steel></costList></RecipeDef></Defs>");

        Assert.Equal(1, DefReferenceAnalyzer.FindInModXml(_mod, "Steel", 1).Count);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_mod, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
