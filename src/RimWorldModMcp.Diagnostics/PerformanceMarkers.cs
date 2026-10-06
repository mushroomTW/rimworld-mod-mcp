using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 判斷一行 Log.Message 是不是效能數據。效能行是 Log.Message 而不是警告或錯誤，
/// 關鍵字分類看不到它，必須靠明確的標記才會被收進診斷。
/// 預設標記為 <c>[perf]</c>；環境變數 RIMWORLD_MOD_MCP_PERF_MARKERS 可追加（以 | 分隔）。
/// Bridge 端有一份同樣的邏輯（bridge/Source/BridgeMod.cs），兩邊的預設標記要一致。
/// </summary>
internal static class PerformanceMarkers
{
    private const string DefaultMarker = "[perf]";

    private static readonly string[] Markers = Load(EnvironmentVariables.PerfMarkers);

    internal static bool Matches(string text) => Matches(text, Markers);

    internal static bool Matches(string text, string[] markers)
        => markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static string[] Load(string? extra)
    {
        var extras = (extra ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [DefaultMarker, .. extras];
    }
}
