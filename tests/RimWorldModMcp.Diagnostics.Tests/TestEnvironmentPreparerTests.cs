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
        Assert.Equal(expected, TestEnvironmentPreparer.SeedFileName(input, Folder));
    }

    [Theory]
    [InlineData("Prefs.xml")]
    [InlineData("KeyPrefs.xml")]
    [InlineData("Mod_NoClass.xml")]
    [InlineData("Mod_Trailing_.xml")]
    [InlineData("Mod_MyMod_MyMod.txt")]
    public void SeedFileName_leaves_other_files_alone(string input)
    {
        Assert.Equal(input, TestEnvironmentPreparer.SeedFileName(input, Folder));
    }

    private const string Prefs = """
        <?xml version="1.0" encoding="utf-8"?>
        <PrefsData>
          <screenWidth>2560</screenWidth>
          <fullscreen>False</fullscreen>
          <uiScale>1.25</uiScale>
        </PrefsData>
        """;

    [Fact]
    public void WithFullscreen_rewrites_only_the_fullscreen_element()
    {
        var result = TestEnvironmentPreparer.WithFullscreen(Prefs, fullscreen: true);

        Assert.Equal(Prefs.Replace("<fullscreen>False</fullscreen>", "<fullscreen>True</fullscreen>"), result);
    }

    [Fact]
    public void WithFullscreen_returns_same_instance_when_already_matching()
    {
        Assert.Same(Prefs, TestEnvironmentPreparer.WithFullscreen(Prefs, fullscreen: false));
    }

    [Fact]
    public void WithFullscreen_leaves_prefs_without_the_element_alone()
    {
        const string noElement = "<PrefsData><screenWidth>1920</screenWidth></PrefsData>";

        Assert.Same(noElement, TestEnvironmentPreparer.WithFullscreen(noElement, fullscreen: true));
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
}
