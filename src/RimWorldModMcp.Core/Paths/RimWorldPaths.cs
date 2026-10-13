namespace RimWorldModMcp.Core.Paths;

/// <summary>
/// 偵測到的 RimWorld 相關路徑。任一欄位為 null 代表該項目不存在或驗證未通過。
/// </summary>
public sealed record RimWorldPaths(
    string? InstallRoot,
    string? Executable,
    string? ManagedDir,
    string? DataDir,
    string? ModsDir,
    string? WorkshopDir,
    string? PlayerLog,
    string? ModsConfig,
    string? PrefsXml)
{
    /// <summary>什麼都沒偵測到。</summary>
    public static RimWorldPaths Empty { get; } = new(null, null, null, null, null, null, null, null, null);

    /// <summary>是否已偵測到可用的遊戲安裝（索引與建置的最低要求）。</summary>
    public bool HasManagedAndData => ManagedDir is not null && DataDir is not null;

    /// <summary>
    /// 安裝目錄是本工具不支援的舊版遊戲時為該版本號（例如 "1.6"；Version.txt 讀不到但是 Unity 結構時為 "pre-1.7"），否則 null。
    /// 本工具只支援 RimWorld 1.7；此時 <see cref="ManagedDir"/> 與 <see cref="Executable"/> 為 null，
    /// 呼叫端應提示使用者更新遊戲，而不是回報「找不到組件」。
    /// </summary>
    public string? UnsupportedVersion { get; init; }

    /// <summary>不支援的舊版遊戲時給使用者的說明，否則 null。</summary>
    public string? UnsupportedMessage => UnsupportedVersion is null
        ? null
        : $"RimWorld {UnsupportedVersion} detected; this tool supports RimWorld 1.7 only.";

    /// <summary>
    /// 各入口使用路徑前先呼叫：舊版遊戲時丟出明確的說明，
    /// 免得呼叫端只看到「找不到組件」而去要使用者設定遊戲路徑。
    /// </summary>
    public void ThrowIfUnsupported()
    {
        if (UnsupportedMessage is { } message)
        {
            throw new NotSupportedException(message);
        }
    }
}

/// <summary>本工具使用的環境變數名稱。</summary>
public static class EnvironmentVariables
{
    /// <summary>傳給 MSBuild 用於解析 RimWorld 組件參考。名稱不含產品前綴，維持原樣。</summary>
    public const string ManagedDir = "RIMWORLD_MANAGED_DIR";

    /// <summary>產品自有的環境變數前綴。</summary>
    private const string Prefix = "RIMWORLD_MOD_MCP_";

    public static string? GamePath => Read("GAME_PATH");

    public static string? PlayerLog => Read("PLAYER_LOG");

    public static string? BridgePort => Read("BRIDGE_PORT");

    /// <summary>額外的效能標記（以 | 分隔）。Bridge 端直接讀同名環境變數，改名要兩邊同步。</summary>
    public static string? PerfMarkers => Read("PERF_MARKERS");

    /// <summary>設定注入子行程時要用的變數名。</summary>
    public static string Name(string suffix) => Prefix + suffix;

    private static string? Read(string suffix)
    {
        var value = Environment.GetEnvironmentVariable(Prefix + suffix);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
