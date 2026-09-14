namespace RimWorldModMcp.Core.Mods;

/// <summary>解析出來的載入順序。</summary>
public sealed record LoadOrder(
    IReadOnlyList<string> Active,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> SkippedLoadAfter);

/// <summary>
/// 依相依關係決定 Mod 的載入順序。
///
/// <para>
/// 硬相依（<c>modDependencies</c>）缺少時列進 <see cref="LoadOrder.Missing"/>，
/// 由呼叫端決定要不要中止；軟排序（<c>loadAfter</c>、<c>loadBefore</c>）缺少時只記錄不影響結果。
/// <c>incompatibleWith</c> 雙方都在選集內則直接拒絕。
/// </para>
/// </summary>
public sealed class LoadOrderResolver
{
    private const string CorePackageId = "ludeon.rimworld";

    public LoadOrder Resolve(IReadOnlyList<ModInfo> selected, IReadOnlyList<ModInfo> available)
    {
        var index = available.ToDictionary(m => m.PackageId, StringComparer.Ordinal);
        var wanted = new HashSet<string>(selected.Select(m => m.PackageId), StringComparer.Ordinal);

        var missing = new List<string>();
        var skippedLoadAfter = new List<string>();

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

        var ordered = TopologicalSort(wanted, index, skippedLoadAfter);

        return new LoadOrder(ordered, missing, skippedLoadAfter);
    }

    private static void CheckIncompatibilities(HashSet<string> wanted, Dictionary<string, ModInfo> index)
    {
        foreach (var id in wanted)
        {
            if (!index.TryGetValue(id, out var mod))
            {
                continue;
            }

            foreach (var conflict in mod.IncompatibleWith)
            {
                if (wanted.Contains(conflict))
                {
                    throw new InvalidOperationException($"偵測到不相容 Mod：{id} 與 {conflict} 不能同時啟用。");
                }
            }
        }
    }

    private static List<string> TopologicalSort(
        HashSet<string> wanted,
        Dictionary<string, ModInfo> index,
        List<string> skippedLoadAfter)
    {
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

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

            foreach (var before in mod.LoadBefore)
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

        // Core 一律排第一，即使它不在 available 裡也要列出來——
        // RimWorld 沒有 Core 就無法啟動。
        ordered.Add(CorePackageId);
        visited.Add(CorePackageId);

        foreach (var id in wanted.Order(StringComparer.Ordinal))
        {
            Visit(id);
        }

        return ordered;

        void Visit(string id)
        {
            if (visited.Contains(id) || !visiting.Add(id))
            {
                // 已完成（visited）：不必重走。進行中（visiting）：遇到循環——
                // 注意展開階段用 visited-set 靜默吸收循環、不會拋出，所以
                // 硬相依的循環也會走到這裡。循環內的順序約束本來就無法全部
                // 滿足，這裡以確定性的 DFS 順序（外層照字母序迭代）收斂。
                return;
            }

            if (!wanted.Contains(id) || !index.TryGetValue(id, out var mod))
            {
                visiting.Remove(id);
                return;
            }

            foreach (var dependency in mod.Dependencies)
            {
                Visit(dependency);
            }

            foreach (var after in mod.LoadAfter)
            {
                if (wanted.Contains(after))
                {
                    Visit(after);
                }
                else if (!skippedLoadAfter.Contains(after, StringComparer.Ordinal))
                {
                    // 軟排序目標不在選集內：記錄下來讓使用者知道，但不視為錯誤。
                    skippedLoadAfter.Add(after);
                }
            }

            // 別的 Mod 宣告了 loadBefore 我們：那些 Mod 必須排在前面。
            if (afterEdgesFromBefore.TryGetValue(id, out var mustComeFirst))
            {
                foreach (var earlier in mustComeFirst)
                {
                    Visit(earlier);
                }
            }

            visiting.Remove(id);
            visited.Add(id);
            ordered.Add(id);
        }
    }
}
