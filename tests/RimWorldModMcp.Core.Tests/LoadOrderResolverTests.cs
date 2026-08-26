using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Core.Tests;

public sealed class LoadOrderResolverTests
{
    private static ModInfo Mod(
        string packageId,
        IReadOnlyList<string>? loadAfter = null,
        IReadOnlyList<string>? loadBefore = null,
        IReadOnlyList<string>? dependencies = null) => new()
    {
        PackageId = packageId,
        Name = packageId,
        Path = "/mods/" + packageId,
        Source = "local",
        LoadAfter = loadAfter ?? [],
        LoadBefore = loadBefore ?? [],
        Dependencies = dependencies ?? [],
    };

    /// <summary>
    /// loadBefore 是 loadAfter 的反向邊，必須被納入排序——否則宣告了
    /// loadBefore 的相伴 Mod 會被排錯，錯誤只會在遊戲內以難懂的形式浮現。
    /// </summary>
    [Fact]
    public void LoadBeforePlacesTheDeclaringModFirst()
    {
        // 取 "z" 開頭讓字母序排它最後：沒有 loadBefore 處理時它一定排錯。
        var early = Mod("z.mustcomefirst", loadBefore: ["a.target"]);
        var target = Mod("a.target");

        var order = new LoadOrderResolver().Resolve([target, early], [target, early]);

        var indexOfEarly = order.Active.ToList().IndexOf("z.mustcomefirst");
        var indexOfTarget = order.Active.ToList().IndexOf("a.target");

        Assert.True(indexOfEarly >= 0 && indexOfTarget >= 0);
        Assert.True(indexOfEarly < indexOfTarget, "宣告 loadBefore 的 Mod 必須排在目標之前");
    }

    [Fact]
    public void LoadAfterOrdersDependenciesFirst()
    {
        var late = Mod("a.late", loadAfter: ["b.early"]);
        var earlyMod = Mod("b.early");

        var order = new LoadOrderResolver().Resolve([late, earlyMod], [late, earlyMod]);

        var active = order.Active.ToList();
        Assert.True(active.IndexOf("b.early") < active.IndexOf("a.late"));
    }

    [Fact]
    public void CoreIsAlwaysFirst()
    {
        var mod = Mod("a.solo");
        var order = new LoadOrderResolver().Resolve([mod], [mod]);

        Assert.Equal("ludeon.rimworld", order.Active[0]);
    }

    [Fact]
    public void MissingHardDependencyIsReported()
    {
        var mod = Mod("a.needy", dependencies: ["b.absent"]);
        var order = new LoadOrderResolver().Resolve([mod], [mod]);

        Assert.Contains("b.absent", order.Missing);
    }
}
