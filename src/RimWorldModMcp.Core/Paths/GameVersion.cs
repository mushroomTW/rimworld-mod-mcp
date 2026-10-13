namespace RimWorldModMcp.Core.Paths;

/// <summary>讀取 RimWorld 安裝目錄下 Version.txt 的版本號，並提供偵測失敗時的退路版本。</summary>
public static class GameVersion
{
    /// <summary>偵測不到遊戲版本時（ModScaffolder 的 supportedVersions、TestEnvironmentPreparer 的 ModsConfig.xml）的退路版本。</summary>
    public const string Fallback = "1.7";

    /// <summary>
    /// 回傳 major.minor（例如 1.7）。檔案不存在或格式不符時回 null。
    /// Version.txt 的內容形如 1.7.4976 rev1043；Mod 的相容性只看前兩段，
    /// 這與 About.xml 的 supportedVersions 用的粒度一致。
    /// </summary>
    public static string? ReadMajorMinor(string? installRoot)
    {
        var full = ReadFull(installRoot);

        if (full is null)
        {
            return null;
        }

        var parts = full.Split(' ', 2)[0].Split('.');

        return $"{parts[0]}.{parts[1]}";
    }

    /// <summary>
    /// 回傳 Version.txt 的完整內容（例如 1.7.4976 rev1043）。檔案不存在或格式不符時回 null。
    /// 遊戲比對 ModsConfig.xml 的 version 時看的是整串，不只 major.minor。
    /// </summary>
    public static string? ReadFull(string? installRoot)
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

        text = text.Trim();

        var parts = text.Split(' ', 2)[0].Split('.');

        return parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)
            ? text
            : null;
    }
}
