namespace RimWorldModMcp.Core.Mods;

/// <summary>解析出來的載入順序。</summary>
public sealed record LoadOrder(
    IReadOnlyList<string> Active,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SkippedLoadAfter,
    IReadOnlyList<string> LoadBeforeCoreConflicts);

/// <summary>
/// 依相依關係決定 Mod 的載入順序。
///
/// <para>
/// 硬相依（modDependencies）缺少時列進 <see cref="LoadOrder.Missing"/>，
/// 由呼叫端決定要不要中止；軟排序（loadAfter、loadBefore）缺少時只記錄不影響結果。
/// incompatibleWith 雙方都在選集內則直接拒絕。
/// 模組間的排序規則形成循環時，回報循環路徑並拒絕產生載入順序。
/// </para>
///
/// <para>
/// 宣告 loadBefore／forceLoadBefore Core 的 Mod（例如 Harmony）排在 Core 之前；
/// 其餘條件逼它排在 Core 之後時，維持在 Core 之後並列進 <see cref="LoadOrder.LoadBeforeCoreConflicts"/>。
/// </para>
/// </summary>
public static class LoadOrderResolver
{
    private const string CorePackageId = "ludeon.rimworld";

    public static LoadOrder Resolve(IReadOnlyList<ModInfo> selected, IReadOnlyList<ModInfo> available)
    {
        var index = available.ToDictionary(m => m.PackageId, StringComparer.Ordinal);
        var wanted = new HashSet<string>(selected.Select(m => m.PackageId), StringComparer.Ordinal);

        var missing = new List<string>();
        var skippedLoadAfter = new List<string>();
        var loadBeforeCoreConflicts = new List<string>();

        // 先把硬相依遞迴展開進選集。
        var frontier = new Queue<string>(wanted);

        while (frontier.Count > 0)
        {
            var id = frontier.Dequeue();

            if (!index.TryGetValue(id, out var mod))
            {
                continue;
            }

            foreach (var dependency in mod.Dependencies)
            {
                if (!index.ContainsKey(dependency))
                {
                    if (!missing.Contains(dependency, StringComparer.Ordinal))
                    {
                        missing.Add(dependency);
                    }

                    continue;
                }

                if (wanted.Add(dependency))
                {
                    frontier.Enqueue(dependency);
                }
            }
        }

        CheckIncompatibilities(wanted, index);

        var ordered = TopologicalSort(wanted, index, skippedLoadAfter, loadBeforeCoreConflicts);

        return new LoadOrder(ordered, missing, skippedLoadAfter, loadBeforeCoreConflicts);
    }

    private static void CheckIncompatibilities(HashSet<string> wanted, Dictionary<string, ModInfo> index)
    {
        foreach (var id in wanted)
        {
            if (!index.TryGetValue(id, out var mod))
            {
                continue;
            }

            var conflict = mod.IncompatibleWith.FirstOrDefault(wanted.Contains);
            if (conflict is not null)
            {
                throw new InvalidOperationException($"Incompatible mods detected: {id} cannot be enabled together with {conflict}.");
            }
        }
    }

    private static List<string> TopologicalSort(
        HashSet<string> wanted,
        Dictionary<string, ModInfo> index,
        List<string> skippedLoadAfter,
        List<string> loadBeforeCoreConflicts)
    {
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visitPath = new List<string>();

        var afterEdgesFromBefore = BuildReverseLoadBeforeEdges(wanted, index);

        // 官方 DLC 先走訪，緊接在 Core 之後——Mod 常不宣告 loadAfter 就 patch DLC 的 Def。
        // DLC 彼此的順序由它們的 forceLoadAfter／forceLoadBefore 決定，不是字母序。
        var roots = wanted
            .OrderBy(id => index.TryGetValue(id, out var mod) && mod.Source == ModInfo.ExpansionSource ? 0 : 1)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        // 宣告 loadBefore／forceLoadBefore Core 的 Mod（例如 Harmony）連同它們的硬相依先走訪，排在 Core 前面。
        // Core 不在 wanted 裡，BuildReverseLoadBeforeEdges 不會替它建反向邊，必須在這裡處理。
        var mustFollowCore = ModsThatMustFollowCore(wanted, index, afterEdgesFromBefore);

        foreach (var id in roots)
        {
            if (id == CorePackageId
                || !index.TryGetValue(id, out var mod)
                || !mod.LoadBefore.Concat(mod.ForceLoadBefore).Contains(CorePackageId, StringComparer.Ordinal))
            {
                continue;
            }

            if (mustFollowCore.Contains(id))
            {
                // 兩邊的條件無法同時滿足：維持排在 Core 之後，回報給使用者而不是默默挑一邊。
                loadBeforeCoreConflicts.Add(id);
                continue;
            }

            Visit(id);
        }

        // 即使 Core 不在 available 裡也要列出來——RimWorld 沒有 Core 就無法啟動。
        ordered.Add(CorePackageId);
        visited.Add(CorePackageId);

        foreach (var id in roots)
        {
            Visit(id);
        }

        return ordered;

        void Visit(string id)
        {
            if (visited.Contains(id))
            {
                return;
            }

            if (!wanted.Contains(id) || !index.TryGetValue(id, out var mod))
            {
                return;
            }

            // 已完成的節點可以共用；仍在目前路徑中的節點才代表循環。
            if (!visiting.Add(id))
            {
                var cycle = visitPath.Skip(visitPath.IndexOf(id)).Append(id);
                throw new InvalidOperationException(
                    $"Cyclic mod ordering rules detected (each mod must load after the next): {string.Join(" -> ", cycle)}. "
                    + "Resolve the conflicting dependency or load-order rules before starting the test.");
            }

            visitPath.Add(id);
            VisitDependencies(mod, wanted, skippedLoadAfter, afterEdgesFromBefore, Visit);

            visitPath.RemoveAt(visitPath.Count - 1);
            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(id);
        }
    }

    private static Dictionary<string, List<string>> BuildReverseLoadBeforeEdges(
        HashSet<string> wanted,
        Dictionary<string, ModInfo> index)
    {
        // loadBefore 是 loadAfter 的反向邊：「X loadBefore Y」等價於「Y loadAfter X」。
        // 先反轉成 after-邊，排序時走同一條路徑——否則宣告了 loadBefore 的
        // 相伴 Mod 會被排錯順序，而錯誤只會在遊戲內以難懂的形式浮現。
        var afterEdgesFromBefore = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var id in wanted)
        {
            if (!index.TryGetValue(id, out var mod))
            {
                continue;
            }

            foreach (var before in mod.LoadBefore.Concat(mod.ForceLoadBefore))
            {
                if (!wanted.Contains(before))
                {
                    continue;
                }

                if (!afterEdgesFromBefore.TryGetValue(before, out var list))
                {
                    afterEdgesFromBefore[before] = list = [];
                }

                list.Add(id);
            }
        }

        return afterEdgesFromBefore;
    }

    /// <summary>
    /// 沿排序用的同一組邊（硬相依、loadAfter、forceLoadAfter、別人對它的 loadBefore）
    /// 找出直接或間接必須排在 Core 之後的 Mod。
    /// </summary>
    private static HashSet<string> ModsThatMustFollowCore(
        HashSet<string> wanted,
        Dictionary<string, ModInfo> index,
        Dictionary<string, List<string>> afterEdgesFromBefore)
    {
        var mustFollow = new HashSet<string>(StringComparer.Ordinal) { CorePackageId };
        bool changed;

        do
        {
            changed = false;

            foreach (var id in wanted)
            {
                if (mustFollow.Contains(id) || !index.TryGetValue(id, out var mod))
                {
                    continue;
                }

                var predecessors = mod.Dependencies
                    .Concat(mod.LoadAfter)
                    .Concat(mod.ForceLoadAfter)
                    .Concat(afterEdgesFromBefore.GetValueOrDefault(id) ?? []);

                if (predecessors.Any(mustFollow.Contains))
                {
                    mustFollow.Add(id);
                    changed = true;
                }
            }
        }
        while (changed);

        return mustFollow;
    }

    private static void VisitDependencies(
        ModInfo mod,
        HashSet<string> wanted,
        List<string> skippedLoadAfter,
        Dictionary<string, List<string>> afterEdgesFromBefore,
        Action<string> visit)
    {
        foreach (var dependency in mod.Dependencies)
        {
            visit(dependency);
        }

        foreach (var after in mod.LoadAfter)
        {
            if (wanted.Contains(after))
            {
                visit(after);
            }
            else if (after != CorePackageId && !skippedLoadAfter.Contains(after, StringComparer.Ordinal))
            {
                // 軟排序目標不在選集內：記錄下來讓使用者知道，但不視為錯誤。
                // （Core 永遠啟用；幾乎每個 Mod 都宣告 loadAfter Core，不列入 skippedLoadAfter）
                skippedLoadAfter.Add(after);
            }
        }

        // 遊戲把 forceLoadAfter 當 loadAfter 排序；目標沒啟用（例如只開 Odyssey）是正常組合，不回報。
        foreach (var after in mod.ForceLoadAfter.Where(wanted.Contains))
        {
            visit(after);
        }

        // 別的 Mod 宣告了 loadBefore 我們：那些 Mod 必須排在前面。
        if (afterEdgesFromBefore.TryGetValue(mod.PackageId, out var mustComeFirst))
        {
            foreach (var earlier in mustComeFirst)
            {
                visit(earlier);
            }
        }
    }
}
