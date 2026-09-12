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
}
