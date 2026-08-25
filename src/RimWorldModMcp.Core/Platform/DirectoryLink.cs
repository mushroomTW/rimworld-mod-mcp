using System.Runtime.InteropServices;

namespace RimWorldModMcp.Core.Platform;

/// <summary>
/// 在 RimWorld 的 Mods 目錄底下建立與移除指向工作區的目錄連結。
///
/// <para>
/// Windows 走 junction（見 <see cref="WindowsJunction"/>，免管理員權限），
/// macOS 與 Linux 走 symbolic link。
/// </para>
/// </summary>
public interface IDirectoryLink
{
    /// <summary>
    /// 確保 <paramref name="linkPath"/> 是一個指向 <paramref name="targetPath"/> 的連結。
    /// 已經是就直接返回（冪等）；被其他東西占用則拋出，絕不覆寫。
    /// </summary>
    void EnsureLink(string linkPath, string targetPath);

    /// <summary>此路徑是否為「本工具建立、且指向指定目標」的連結。</summary>
    bool IsOwnedLink(string linkPath, string targetPath);

    /// <summary>
    /// 移除本工具建立的連結。不是本工具建立的、或指向別處的，一律不動作。
    /// <b>只移除連結本身，永遠不會碰到目標目錄的內容。</b>
    /// </summary>
    void RemoveLink(string linkPath, string targetPath);
}

/// <inheritdoc cref="IDirectoryLink"/>
public sealed class DirectoryLink : IDirectoryLink
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
        try
        {
            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is null)
            {
                return false;
            }

            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved.FullName));
            var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));

            var comparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            return string.Equals(actual, expected, comparison);
        }
        catch (IOException)
        {
            // 連結已斷掉之類的狀況：無法確認指向，就不當成自家連結。
            return false;
        }
    }

    public void RemoveLink(string linkPath, string targetPath)
    {
        if (!IsOwnedLink(linkPath, targetPath))
        {
            return;
        }

        try
        {
            // 關鍵安全點：一定要用非遞迴的 overload。
            // Directory.Delete(path, recursive: true) 會走進 junction 把「目標目錄」的內容刪光，
            // 那等於毀掉使用者的 Mod 原始碼。非遞迴版只移除 reparse point 本身。
            Directory.Delete(linkPath);
        }
        catch (IOException)
        {
            // 清理是盡力而為，失敗不應讓整個停止流程中斷。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
