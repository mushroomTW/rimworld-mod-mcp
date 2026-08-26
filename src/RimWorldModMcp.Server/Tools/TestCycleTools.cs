using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>在隔離環境中測試 Mod 並讀取診斷。</summary>
[McpServerToolType]
public sealed class TestCycleTools(TestCycleService testCycle, DiagnosticStore diagnostics)
{
    [McpServerTool(Name = "run_test_cycle", UseStructuredContent = true, Idempotent = false)]
    [Description("在隔離環境啟動 RimWorld 測試指定的 Mod。使用獨立的存檔目錄，不會動到使用者的存檔與設定。遊戲已在執行時會拒絕啟動。")]
    public TestSessionResult RunTestCycle(
        [Description("要測試的 Mod 目錄，必須在已登記的工作區內。")]
        string path,
        [Description("要一併啟用的其他 Mod 的 packageId 清單。")]
        string[]? companion_mods = null,
        [Description("是否用 -quicktest 直接進入測試地圖，跳過主選單。")]
        bool quicktest = true)
    {
        return ToResult(testCycle.Start(path, companion_mods, quicktest));
    }

    [McpServerTool(Name = "test_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("回報目前測試場次的狀態，包含 Bridge 與診斷 daemon 是否正常運作。")]
    public TestSessionResult TestStatus() => ToResult(testCycle.Status());

    [McpServerTool(Name = "stop_test", UseStructuredContent = true, Destructive = true)]
    [Description("停止測試場次：移除臨時連結、終止診斷 daemon、清理暫存存檔。需要 confirm=true。")]
    public TestSessionResult StopTest(
        [Description("必須明確傳 true 才會執行。")]
        bool confirm = false,
        [Description("是否一併終止遊戲行程。預設 false，讓使用者自己關閉遊戲。")]
        bool terminate_game = false)
    {
        if (!confirm)
        {
            throw new UnauthorizedAccessException("停止測試會移除連結並終止 daemon，需要 confirm=true。");
        }

        return ToResult(testCycle.Stop(terminate_game));
    }

    [McpServerTool(Name = "list_test_diagnostics", UseStructuredContent = true, ReadOnly = true)]
    [Description("列出本次測試收集到的錯誤與警告。同一個錯誤會合併並累計次數。")]
    public ListDiagnosticsResult ListTestDiagnostics(
        [Description("只回傳指定類型：error、warning、diagnostic、loaded_mods 或 performance。")]
        string? type = null,
        [Description("每筆內容的字元上限，避免長堆疊灌爆輸出。")]
        int max_text_length = 2000)
    {
        var records = diagnostics.Read();

        if (!string.IsNullOrEmpty(type))
        {
            records = [.. records.Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))];
        }

        var limit = Math.Clamp(max_text_length, 100, 20000);

        return new ListDiagnosticsResult
        {
            Results = [.. records.Select(r => ToSummary(r, limit))],
            Count = records.Count,
            ErrorCount = records.Count(r => r.Type == "error"),
            WarningCount = records.Count(r => r.Type == "warning"),
        };
    }

    [McpServerTool(Name = "get_test_diagnostic", UseStructuredContent = true, ReadOnly = true)]
    [Description("依 hash 取得單一診斷的完整內容，包含未截斷的堆疊。")]
    public DiagnosticSummary GetTestDiagnostic(
        [Description("診斷的 hash，來自 list_test_diagnostics。")]
        string diagnostic_hash)
    {
        var record = diagnostics.Find(diagnostic_hash)
            ?? throw new KeyNotFoundException($"找不到診斷：{diagnostic_hash}");

        return ToSummary(record, int.MaxValue);
    }

    private static TestSessionResult ToResult(TestSession session) => new()
    {
        State = session.State,
        RunId = session.RunId,
        Mod = session.Mod,
        ActiveMods = session.ActiveMods,
        SkippedLoadAfter = session.SkippedLoadAfter,
        SaveData = session.SaveData,
        PlayerLog = session.PlayerLog,
        BridgePort = session.BridgePort,
        BridgeState = session.Bridge?.State,
        BridgeReason = session.Bridge?.Reason,
        DaemonState = session.Daemon?.State,
        DaemonReason = session.Daemon?.Reason,
        GamePid = session.GamePid,
        PreviousRun = session.PreviousRun,
        TerminatedDaemon = session.Terminated?.Daemon,
        TerminatedGame = session.Terminated?.Game,
    };

    private static DiagnosticSummary ToSummary(DiagnosticRecord record, int maxTextLength) => new()
    {
        Hash = record.Hash,
        Type = record.Type,
        FirstLine = record.FirstLine,
        Text = record.Text.Length > maxTextLength ? record.Text[..maxTextLength] : record.Text,
        TextTruncated = record.Text.Length > maxTextLength,
        Source = record.Source,
        RunId = record.RunId,
        Count = record.Count,
        At = record.At,
    };
}

/// <summary>測試場次的狀態。</summary>
public sealed record TestSessionResult
{
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("mod")]
    public string? Mod { get; init; }

    [JsonPropertyName("active_mods")]
    public IReadOnlyList<string> ActiveMods { get; init; } = [];

    /// <summary>選集中不存在、因此被略過的軟排序目標。</summary>
    [JsonPropertyName("skipped_load_after")]
    public IReadOnlyList<string> SkippedLoadAfter { get; init; } = [];

    [JsonPropertyName("save_data")]
    public string? SaveData { get; init; }

    [JsonPropertyName("player_log")]
    public string? PlayerLog { get; init; }

    [JsonPropertyName("bridge_port")]
    public int BridgePort { get; init; }

    [JsonPropertyName("bridge_state")]
    public string? BridgeState { get; init; }

    [JsonPropertyName("bridge_reason")]
    public string? BridgeReason { get; init; }

    /// <summary><c>started</c>、<c>reused</c> 或 <c>unavailable</c>。</summary>
    [JsonPropertyName("daemon_state")]
    public string? DaemonState { get; init; }

    /// <summary>daemon 不可用的原因。有值時 Bridge 診斷會失效，只剩 Player.log。</summary>
    [JsonPropertyName("daemon_reason")]
    public string? DaemonReason { get; init; }

    [JsonPropertyName("game_pid")]
    public int? GamePid { get; init; }

    [JsonPropertyName("previous_run")]
    public string? PreviousRun { get; init; }

    [JsonPropertyName("terminated_daemon")]
    public bool? TerminatedDaemon { get; init; }

    [JsonPropertyName("terminated_game")]
    public bool? TerminatedGame { get; init; }
}

/// <summary>診斷清單。</summary>
public sealed record ListDiagnosticsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DiagnosticSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("error_count")]
    public required int ErrorCount { get; init; }

    [JsonPropertyName("warning_count")]
    public required int WarningCount { get; init; }
}

/// <summary>一筆診斷。</summary>
public sealed record DiagnosticSummary
{
    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("first_line")]
    public required string FirstLine { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("text_truncated")]
    public required bool TextTruncated { get; init; }

    /// <summary><c>bridge</c> 或 <c>player.log</c>。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    /// <summary>這個診斷在本次測試中出現的次數。</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("at")]
    public required long At { get; init; }
}
