using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Core.Mods;

/// <summary>列舉已安裝的 Mod 與內建的 Core／DLC。</summary>
public sealed class ModCatalog(IRimWorldLocator locator)
{
    /// <summary>
    /// 列出使用者安裝的 Mod（本機 Mods 目錄與 Steam Workshop）。
    /// 不含 Core 與 DLC——那些在 <c>Data/</c> 底下，由 <see cref="BuiltinPacks"/> 提供。
    /// </summary>
    public IReadOnlyList<ModInfo> Installed()
    {
        var paths = locator.Detect();
        var mods = new List<ModInfo>();

        Collect(paths.ModsDir, "local", mods);
        Collect(paths.WorkshopDir, "workshop", mods);

        return [.. mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.PackageId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 列出 Core 與已安裝的 DLC。
    /// 這些必須另外處理：它們住在 <c>Data/</c>，不在 Mods 目錄裡，
    /// 但測試場次的 activeMods 清單需要它們。
    /// </summary>
    public IReadOnlyList<ModInfo> BuiltinPacks()
    {
        var paths = locator.Detect();

        if (paths.DataDir is null)
        {
            return [];
        }

        var packs = new List<ModInfo>();

        foreach (var directory in Directory.GetDirectories(paths.DataDir).Order())
        {
            var info = AboutXml.Parse(directory, IsCore(directory) ? "core" : "expansion");

            if (info is not null)
            {
                packs.Add(info);
            }
        }

        return packs;
    }

    /// <summary>依 packageId 找出一個已安裝的 Mod。</summary>
    public ModInfo Find(string packageId)
    {
        var normalised = packageId.Trim().ToLowerInvariant();

        return Installed().FirstOrDefault(m => m.PackageId == normalised)
            ?? throw new KeyNotFoundException($"找不到已安裝 Mod：{packageId}");
    }

    private static bool IsCore(string directory)
        => string.Equals(Path.GetFileName(directory), "Core", StringComparison.OrdinalIgnoreCase);

    private static void Collect(string? root, string source, List<ModInfo> into)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.GetDirectories(root))
        {
            var info = AboutXml.Parse(directory, source);

            if (info is not null)
            {
                into.Add(info);
            }
        }
    }
}
