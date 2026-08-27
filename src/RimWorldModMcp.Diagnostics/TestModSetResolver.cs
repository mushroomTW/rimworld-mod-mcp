using RimWorldModMcp.Core.Mods;

namespace RimWorldModMcp.Diagnostics;

/// <summary>解析完成的測試選集。</summary>
public sealed record TestModSet(
    ModInfo Mod,
    IReadOnlyList<string> ActiveMods,
    IReadOnlyList<string> SkippedLoadAfter);

/// <summary>
/// 決定一次測試場次要啟用哪些 Mod、以什麼順序。
///
/// <para>
/// 從 <see cref="TestCycleService"/> 拆出來的第一塊：選集與載入順序是純資料
/// 計算，不碰檔案系統也不碰行程，單獨存在才能單獨測試。
/// </para>
/// </summary>
public sealed class TestModSetResolver(ModCatalog catalog, LoadOrderResolver resolver)
{
    public TestModSet Resolve(ModInfo mod, IReadOnlyList<string>? companionMods)
    {
        var available = new List<ModInfo>(catalog.BuiltinPacks());
        available.AddRange(catalog.Installed());
        available.Add(mod);

        var selected = new List<ModInfo> { mod };

        foreach (var companion in companionMods ?? [])
        {
            var match = available.FirstOrDefault(m => m.PackageId == companion.Trim().ToLowerInvariant());

            if (match is null)
            {
                throw new KeyNotFoundException($"找不到指定的相伴 Mod：{companion}");
            }

            selected.Add(match);
        }

        var order = resolver.Resolve(selected, available);

        if (order.Missing.Count > 0)
        {
            throw new InvalidOperationException($"缺少必要的相依 Mod：{string.Join("、", order.Missing)}");
        }

        return new TestModSet(mod, order.Active, order.SkippedLoadAfter);
    }
}
