using System.Runtime.InteropServices;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// <see cref="DirectoryLink.RemoveLink"/> 的結果。
///
/// <para>
/// 「已消失」與「存在但我們拒絕碰」必須能區分：把後者當成功，
/// 呼叫端會從追蹤狀態移除它，一個佔著連結名稱的陌生目錄就再也沒人回報。
/// </para>
/// </summary>
public enum LinkRemoval
{
    /// <summary>連結已移除，或本來就不存在。</summary>
    Removed,

    /// <summary>路徑上存在的東西不是本工具的連結，拒絕動它。</summary>
    NotOurs,

    /// <summary>是自家連結但移除失敗（最常見：遊戲仍透過它載入組件）。</summary>
    Failed,
}

/// <summary>
/// 在 RimWorld 的 Mods 目錄底下建立與移除指向工作區的目錄連結。
///
/// <para>
/// Windows 走 junction（見 <see cref="WindowsJunction"/>，免管理員權限），
/// macOS 與 Linux 走 symbolic link。
/// </para>
/// </summary>
public sealed class DirectoryLink
{
    /// <summary>本工具建立的連結一律帶這個前綴，是三重防線的第一重。</summary>
    public const string LinkPrefix = "RimWorldModMcp-";

    /// <summary>名稱是否帶有本工具的連結前綴。</summary>
    public static bool HasOwnedPrefix(string linkPath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(linkPath));
        return name.StartsWith(LinkPrefix, StringComparison.Ordinal);
    }

    public void EnsureLink(string linkPath, string targetPath)
    {
        var target = Path.GetFullPath(targetPath);

        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"連結目標不存在：{target}");
        }

        // 已經是指向同一個目標的自家連結就什麼都不用做。
        if (IsOwnedLink(linkPath, target))
        {
            return;
        }

        // 走到這裡代表路徑被別的東西占著——可能是使用者自己的 Mod 目錄，
        // 也可能是指向別處的舊連結。兩種都不允許覆寫。
        if (Directory.Exists(linkPath) || File.Exists(linkPath))
        {
            throw new IOException($"拒絕覆寫既有路徑：{linkPath}");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            WindowsJunction.Create(linkPath, target);
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, target);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI instance service method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "DI instance service method")]
    public bool IsOwnedLink(string linkPath, string targetPath)
    {
        // 第一重：名稱前綴。沒有前綴的一律不認，避免誤動使用者自己的 Mod。
        if (!HasOwnedPrefix(linkPath))
        {
            return false;
        }

        var info = new DirectoryInfo(linkPath);

        // 第二重：必須真的是 reparse point / symlink。
        // 注意 Windows 的 junction 在 .NET 與 Python 都不會被當成 symlink，
        // 所以判定基準是 ReparsePoint 屬性而不是「是不是 symlink」。
        if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        // 第三重：解析後必須確實指向預期目標。
        //
        // 這裡要兩邊用同樣的方式解析才能比對。returnFinalTarget: true 會穿透
        // 所有層級的重新導向——企業的資料夾重新導向、OneDrive 已知資料夾移動、
        // 打包應用程式的容器虛擬化都會讓「完全解析後的路徑」與當初寫入的路徑
        // 長得完全不同。只解析其中一邊會讓自家連結被誤判成別人的，
        // 於是清理時被跳過，在使用者的 Mods 目錄留下孤兒連結。
        try
        {
            var expected = Normalise(targetPath);

            // 先比對「直接目標」，也就是當初寫進 reparse point 的原始字串。
            var immediate = info.ResolveLinkTarget(returnFinalTarget: false);

            if (immediate is not null && PathsEqual(Normalise(immediate.FullName), expected))
            {
                return true;
            }

            // 再比對「完全解析後」的形式，兩邊都解析，讓重新導向在雙方同時展開。
            var actualFinal = info.ResolveLinkTarget(returnFinalTarget: true);

            if (actualFinal is null)
            {
                return false;
            }

            return PathsEqual(Normalise(actualFinal.FullName), Normalise(FinalTargetOf(targetPath)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 連結已斷掉、或無權讀取屬性：無法確認指向，就不當成自家連結。
            return false;
        }
    }

    /// <summary>把預期目標本身也完全解析一次，讓兩邊站在同一個基準上比對。</summary>
    private static string FinalTargetOf(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);

            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
            }

            // 目標本身不是連結時，仍可能位於被重新導向的路徑底下。
            // 用實際存在的父目錄回推真實位置。
            return info.Exists ? info.FullName : path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    private static string Normalise(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right)
        => string.Equals(left, right, PathText.Comparison);

    public LinkRemoval RemoveLink(string linkPath, string targetPath)
    {
        if (!IsOwnedLink(linkPath, targetPath))
        {
            // 路徑上什麼都沒有＝已清乾淨；有東西但不是自家連結＝拒絕碰，
            // 但要讓呼叫端知道那裡佔著一個不是我們的東西。
            return Directory.Exists(linkPath) || File.Exists(linkPath)
                ? LinkRemoval.NotOurs
                : LinkRemoval.Removed;
        }

        try
        {
            // 關鍵安全點：一定要用非遞迴的 overload。
            // Directory.Delete(path, recursive: true) 會走進 junction 把「目標目錄」的內容刪光，
            // 那等於毀掉使用者的 Mod 原始碼。非遞迴版只移除 reparse point 本身。
            Directory.Delete(linkPath);
            return LinkRemoval.Removed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 移除失敗最常見的原因是遊戲仍在執行、透過這個連結載入著組件。
            // 不再靜默吞掉——呼叫端要能把殘留回報給使用者。
            return LinkRemoval.Failed;
        }
    }
}
