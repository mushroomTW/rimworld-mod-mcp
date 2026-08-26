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
/// 由呼叫端決定要不要中止；軟排序（<c>loadAfter</c>）缺少時只記錄不影響結果。
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
        var visited = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Core 一律排第一，即使它不在 available 裡也要列出來——
        // RimWorld 沒有 Core 就無法啟動。
        ordered.Add(CorePackageId);
        visited[CorePackageId] = true;

        foreach (var id in wanted.Order(StringComparer.Ordinal))
        {
            Visit(id);
        }

        return ordered;

        void Visit(string id)
        {
            if (visited.TryGetValue(id, out var completed))
            {
                // 進行中（false）代表遇到循環。硬相依的循環在展開階段就會爆，
                // 這裡只可能是 loadAfter 造成的軟循環，忽略即可。
                return;
            }

            if (!wanted.Contains(id) || !index.TryGetValue(id, out var mod))
            {
                return;
            }

            visited[id] = false;

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

            visited[id] = true;

            if (!ordered.Contains(id, StringComparer.Ordinal))
            {
                ordered.Add(id);
            }
        }
    }
}
