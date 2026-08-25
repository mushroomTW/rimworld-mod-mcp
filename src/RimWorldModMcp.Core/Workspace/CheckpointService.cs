using System.Security.Cryptography;
using System.Text;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Core.Workspace;

/// <summary>一份 Mod 快照。</summary>
public sealed record Checkpoint(string Id, string Path, DateTime CreatedUtc);

/// <summary>
/// Mod 的本機快照與還原。
///
/// <para>
/// 快照與還原刻意使用<b>同一組</b>排除規則。Python 版在快照時排除 <c>bin</c>／<c>obj</c>
/// 但還原時不排除，結果是還原之後那兩個目錄會消失且不會被復原——
/// 這裡讓兩邊對稱，還原時保留被排除的目錄不動。
/// </para>
/// </summary>
public sealed class CheckpointService(StoreDirectories store, WorkspaceRegistry workspaces)
{
    /// <summary>建置產物與版本控制目錄不進快照，也不在還原時被刪除。</summary>
    private static readonly string[] ExcludedFolders = ["bin", "obj", ".git", ".vs"];

    public Checkpoint Create(string modPath)
    {
        var mod = workspaces.AllowedMod(modPath);
        var root = SnapshotRoot(mod);

        Directory.CreateDirectory(root);

        var now = DateTime.UtcNow;

        // 時間戳到毫秒加上隨機尾碼：Python 版只到秒，同一秒內連續兩次快照會撞名。
        var id = $"{now:yyyyMMdd-HHmmss-fff}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        var target = Path.Combine(root, id);

        CopyTree(mod, target);

        return new Checkpoint(id, target, now);
    }

    public IReadOnlyList<Checkpoint> List(string modPath)
    {
        var mod = workspaces.AllowedMod(modPath);
        var root = SnapshotRoot(mod);

        if (!Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.GetDirectories(root)
            .Select(d => new Checkpoint(Path.GetFileName(d), d, Directory.GetCreationTimeUtc(d)))
            .OrderByDescending(c => c.CreatedUtc)];
    }

    /// <summary>還原快照。會先自動建立一份還原前的快照，以免誤操作無法回頭。</summary>
    public (string Restored, string PreRestoreCheckpoint) Restore(string modPath, string checkpointId)
    {
        var mod = workspaces.AllowedMod(modPath);
        var source = Path.Combine(SnapshotRoot(mod), checkpointId);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"找不到快照：{checkpointId}");
        }

        var safety = Create(mod);

        // 清掉現有內容，但保留被排除的目錄——那些本來就不在快照裡，
        // 刪掉的話使用者的建置產物與 git 歷史會一起消失。
        foreach (var directory in Directory.GetDirectories(mod))
        {
            if (IsExcluded(Path.GetFileName(directory)))
            {
                continue;
            }

            Directory.Delete(directory, recursive: true);
        }

        foreach (var file in Directory.GetFiles(mod))
        {
            File.Delete(file);
        }

        CopyTree(source, mod);

        return (mod, safety.Id);
    }

    private string SnapshotRoot(string modPath)
    {
        // 以路徑雜湊區分不同 Mod，避免同名 Mod 的快照混在一起。
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modPath)))[..12];
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(modPath));

        return Path.Combine(store.SnapshotsDirectory, $"{name}-{digest}");
    }

    private static bool IsExcluded(string name)
        => ExcludedFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var directory in Directory.GetDirectories(source))
        {
            if (IsExcluded(Path.GetFileName(directory)))
            {
                continue;
            }

            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }
}
