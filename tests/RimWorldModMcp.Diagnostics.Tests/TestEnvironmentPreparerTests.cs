using System.Xml.Linq;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

public sealed class TestEnvironmentPreparerTests
{
    private const string Folder = "RimWorldModMcp-Test-author.mymod";

    [Theory]
    [InlineData("Mod_MyMod_MyModSettings.xml", "Mod_RimWorldModMcp-Test-author.mymod_MyModSettings.xml")]
    [InlineData("Mod_2900000000_MyMod.xml", "Mod_RimWorldModMcp-Test-author.mymod_MyMod.xml")]
    [InlineData("Mod_My_Mod_Folder_MyMod.xml", "Mod_RimWorldModMcp-Test-author.mymod_MyMod.xml")]
    [InlineData("Mod_RimWorldModMcp-Test-author.mymod_MyMod.xml", "Mod_RimWorldModMcp-Test-author.mymod_MyMod.xml")]
    public void SeedFileName_rewrites_mod_settings_folder_segment(string input, string expected)
    {
        Assert.Equal(expected, TestEnvironmentPreparer.SeedFileName(input, Folder, []));
    }

    [Theory]
    [InlineData("Mod_2009463077_FGLSettings.xml")]
    [InlineData("Mod_my_companion_CompanionSettings.xml")]
    public void SeedFileName_keeps_settings_of_other_active_mods(string input)
    {
        // 附屬 Mod 在測試場次裡沿用原本的資料夾名，改名反而讓它讀不到設定。
        string[] otherFolders = ["2009463077", "my_companion"];

        Assert.Equal(input, TestEnvironmentPreparer.SeedFileName(input, Folder, otherFolders));
    }

    [Theory]
    [InlineData("Prefs.xml")]
    [InlineData("KeyPrefs.xml")]
    [InlineData("Mod_NoClass.xml")]
    [InlineData("Mod_Trailing_.xml")]
    [InlineData("Mod_MyMod_MyMod.txt")]
    public void SeedFileName_leaves_other_files_alone(string input)
    {
        Assert.Equal(input, TestEnvironmentPreparer.SeedFileName(input, Folder, []));
    }

    private const string Prefs = """
        <?xml version="1.0" encoding="utf-8"?>
        <PrefsData>
          <screenWidth>1600</screenWidth>
          <screenHeight>900</screenHeight>
          <fullscreen>False</fullscreen>
          <uiScale>1.25</uiScale>
        </PrefsData>
        """;

    [Fact]
    public void ApplyFullscreen_turns_fullscreen_on_and_leaves_the_rest_alone()
    {
        var root = XDocument.Parse(Prefs).Root!;

        TestEnvironmentPreparer.ApplyFullscreen(root);

        Assert.Equal("True", root.Element("fullscreen")!.Value);
        Assert.Equal("1600", root.Element("screenWidth")!.Value);
        Assert.Equal("900", root.Element("screenHeight")!.Value);
        Assert.Equal("1.25", root.Element("uiScale")!.Value);
    }

    [Fact]
    public void ApplyFullscreen_adds_a_missing_element()
    {
        // RimWorld 不一定寫出預設值（false）的欄位，缺了也要補上。
        var root = XElement.Parse("<PrefsData><uiScale>1</uiScale></PrefsData>");

        TestEnvironmentPreparer.ApplyFullscreen(root);

        Assert.Equal("True", root.Element("fullscreen")!.Value);
    }

    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\Games\RimWorld" : "/opt/rimworld";
    private static readonly string ModsDir = Path.Combine(Root, "Mods");

    [Fact]
    public void IsDirectlyUnderModsDirectory_true_for_mod_folder_in_mods()
    {
        Assert.True(TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(Path.Combine(ModsDir, "RimAgent"), ModsDir));
    }

    [Fact]
    public void IsDirectlyUnderModsDirectory_ignores_trailing_separators()
    {
        var mod = Path.Combine(ModsDir, "RimAgent") + Path.DirectorySeparatorChar;
        var mods = ModsDir + Path.DirectorySeparatorChar;

        Assert.True(TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(mod, mods));
    }

    [Fact]
    public void IsDirectlyUnderModsDirectory_follows_platform_case_rules()
    {
        var mod = Path.Combine(Root, "mods", "RimAgent");

        Assert.Equal(!OperatingSystem.IsLinux(), TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(mod, ModsDir));
    }

    [Fact]
    public void IsDirectlyUnderModsDirectory_false_for_nested_folder()
    {
        Assert.False(TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(Path.Combine(ModsDir, "Dev", "RimAgent"), ModsDir));
    }

    [Fact]
    public void IsDirectlyUnderModsDirectory_false_for_workshop_and_mods_dir_itself()
    {
        var workshop = Path.Combine(Root, "..", "..", "workshop", "content", "294100", "123456");

        Assert.False(TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(workshop, ModsDir));
        Assert.False(TestEnvironmentPreparer.IsDirectlyUnderModsDirectory(ModsDir, ModsDir));
    }

    /// <summary>
    /// 清不掉的連結必須被回報。
    ///
    /// <para>
    /// 換場次時舊場次的連結若清不掉（最常見原因是崩潰後殘留的遊戲行程仍持有組件），
    /// 把結果丟掉就等於那個連結從此沒人追蹤——它會永久留在使用者的 Mods 目錄，
    /// 而 <c>stop_test</c> 不會再嘗試補清，也不會回報。呼叫端必須把回傳值
    /// 併進新場次的追蹤清單。
    /// </para>
    /// </summary>
    [Fact]
    public void RemoveLinksReportsWhatItCouldNotRemove()
    {
        var root = Path.Combine(Path.GetTempPath(), "rwmm-prep-" + Guid.NewGuid().ToString("n")[..12]);

        try
        {
            var store = new RimWorldModMcp.Core.Paths.StoreDirectories(
                Path.Combine(root, "data"), Path.Combine(root, "cache"));

            var preparer = new TestEnvironmentPreparer(
                store,
                new BridgeBuilder(store, new RimWorldModMcp.Core.Paths.RimWorldLocator()));

            // 佔著連結名稱、但不是我們的連結：RemoveLink 會拒絕動它（NotOurs）。
            var intruder = Path.Combine(root, "RimWorldModMcp-Test-intruder");
            Directory.CreateDirectory(intruder);

            var remaining = TestEnvironmentPreparer.RemoveLinks([new TestLink(intruder, Path.Combine(root, "target"))]);

            var link = Assert.Single(remaining);

            Assert.Equal(intruder, link.Link);
            Assert.True(Directory.Exists(intruder), "不是我們的目錄不能被刪掉");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>已經不存在的連結不算「清不掉」——那只是先前就清乾淨了。</summary>
    [Fact]
    public void RemoveLinksIgnoresLinksThatAreAlreadyGone()
    {
        var root = Path.Combine(Path.GetTempPath(), "rwmm-prep-" + Guid.NewGuid().ToString("n")[..12]);

        try
        {
            var store = new RimWorldModMcp.Core.Paths.StoreDirectories(
                Path.Combine(root, "data"), Path.Combine(root, "cache"));

            var preparer = new TestEnvironmentPreparer(
                store,
                new BridgeBuilder(store, new RimWorldModMcp.Core.Paths.RimWorldLocator()));

            var missing = Path.Combine(root, "RimWorldModMcp-Test-missing");

            Assert.Empty(TestEnvironmentPreparer.RemoveLinks([new TestLink(missing, Path.Combine(root, "target"))]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
