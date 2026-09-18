namespace RimWorldModMcp.Core.Paths;

/// <summary>讀取 RimWorld 安裝目錄下 <c>Version.txt</c> 的版本號。</summary>
public static class GameVersion
{
    /// <summary>
    /// 回傳 <c>major.minor</c>（例如 <c>1.6</c>）。檔案不存在或格式不符時回 <c>null</c>。
    /// Version.txt 的內容形如 <c>1.6.4871 rev590</c>；Mod 的相容性只看前兩段，
    /// 這與 About.xml 的 supportedVersions 用的粒度一致。
    /// </summary>
    public static string? ReadMajorMinor(string? installRoot)
    {
        if (installRoot is null)
        {
            return null;
        }

        string text;

        try
        {
            text = File.ReadAllText(Path.Combine(installRoot, "Version.txt"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var parts = text.Trim().Split(' ', 2)[0].Split('.');

        return parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)
            ? $"{parts[0]}.{parts[1]}"
            : null;
    }
}
