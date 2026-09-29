using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Core.Tests;

public sealed class LoadOrderResolverTests
{
    private static ModInfo Mod(
        string packageId,
        IReadOnlyList<string>? loadAfter = null,
        IReadOnlyList<string>? loadBefore = null,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<string>? forceLoadAfter = null,
        IReadOnlyList<string>? forceLoadBefore = null,
        string source = "local") => new()
    {
        PackageId = packageId,
        Name = packageId,
        Path = "/mods/" + packageId,
        Source = source,
        LoadAfter = loadAfter ?? [],
        LoadBefore = loadBefore ?? [],
        Dependencies = dependencies ?? [],
        ForceLoadAfter = forceLoadAfter ?? [],
        ForceLoadBefore = forceLoadBefore ?? [],
    };

    /// <summary>1.6 的 Data/ 內 DLC 實際宣告的排序（Core 省略）。</summary>
    private static ModInfo[] Expansions() =>
    [
        Mod("ludeon.rimworld.anomaly", source: ModInfo.ExpansionSource,
            forceLoadAfter: ["ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech"],
            forceLoadBefore: ["ludeon.rimworld.odyssey"]),
        Mod("ludeon.rimworld.biotech", source: ModInfo.ExpansionSource,
            forceLoadAfter: ["ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology"],
            forceLoadBefore: ["ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"]),
        Mod("ludeon.rimworld.ideology", source: ModInfo.ExpansionSource,
            forceLoadAfter: ["ludeon.rimworld", "ludeon.rimworld.royalty"],
            forceLoadBefore: ["ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"]),
        Mod("ludeon.rimworld.odyssey", source: ModInfo.ExpansionSource,
            forceLoadAfter: ["ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly"]),
        Mod("ludeon.rimworld.royalty", source: ModInfo.ExpansionSource,
            forceLoadAfter: ["ludeon.rimworld"],
            forceLoadBefore: ["ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey"]),
    ];

    /// <summary>
    /// 回報的 bug：DLC 照字母序排，Odyssey 落在 Royalty 前面，Odyssey 的 Def 找不到 Royalty 的父節點。
    /// 也不能排在一般 Mod 之後——Mod 常不宣告 loadAfter 就 patch DLC 的 Def。
    /// </summary>
    [Fact]
    public void ExpansionsFollowCoreInForcedOrderBeforeOtherMods()
    {
        var mod = Mod("a.mod");
        ModInfo[] available = [mod, .. Expansions()];

        var order = LoadOrderResolver.Resolve(available, available);

        Assert.Equal(
            ["ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech",
             "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey", "a.mod"],
            order.Active);
    }

    /// <summary>只宣告 forceLoadBefore（目標方沒有對應的 forceLoadAfter）也必須生效。</summary>
    [Fact]
    public void ForceLoadBeforePlacesTheDeclaringModFirst()
    {
        // 取 "z" 開頭讓字母序排它最後：沒有 forceLoadBefore 的反向邊時它一定排錯。
        var early = Mod("z.mustcomefirst", forceLoadBefore: ["a.target"]);
        var target = Mod("a.target");

        var order = LoadOrderResolver.Resolve([target, early], [target, early]);

        Assert.Equal(["ludeon.rimworld", "z.mustcomefirst", "a.target"], order.Active);
    }

    /// <summary>forceLoad 目標沒啟用（例如只開 Odyssey）是正常組合，不該被當成略過的軟排序回報。</summary>
    [Fact]
    public void ForceLoadTargetsOutsideTheSelectionAreNotReported()
    {
        var odyssey = Expansions().Single(m => m.PackageId == "ludeon.rimworld.odyssey");

        var order = LoadOrderResolver.Resolve([odyssey], Expansions());

        Assert.Equal(["ludeon.rimworld", "ludeon.rimworld.odyssey"], order.Active);
        Assert.Empty(order.SkippedLoadAfter);
    }

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

        var order = LoadOrderResolver.Resolve([target, early], [target, early]);

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

        var order = LoadOrderResolver.Resolve([late, earlyMod], [late, earlyMod]);

        var active = order.Active.ToList();
        Assert.True(active.IndexOf("b.early") < active.IndexOf("a.late"));
    }

    [Fact]
    public void CoreIsAlwaysFirst()
    {
        var mod = Mod("a.solo");
        var order = LoadOrderResolver.Resolve([mod], [mod]);

        Assert.Equal("ludeon.rimworld", order.Active[0]);
    }

    /// <summary>幾乎每個 Mod 都 loadAfter Core；Core 永遠啟用，不是「被略過的軟排序」。</summary>
    [Fact]
    public void LoadAfterCoreIsNotReportedAsSkipped()
    {
        var mod = Mod("a.solo", loadAfter: ["ludeon.rimworld", "ludeon.rimworld.royalty"]);
        var order = LoadOrderResolver.Resolve([mod], [mod]);

        Assert.Equal(["ludeon.rimworld.royalty"], order.SkippedLoadAfter);
    }

    [Fact]
    public void MissingHardDependencyIsReported()
    {
        var mod = Mod("a.needy", dependencies: ["b.absent"]);
        var order = LoadOrderResolver.Resolve([mod], [mod]);

        Assert.Contains("b.absent", order.Missing);
    }
}
