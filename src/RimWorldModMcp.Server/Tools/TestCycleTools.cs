using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>在隔離環境中測試 Mod 並讀取診斷。</summary>
[McpServerToolType]
public sealed class TestCycleTools(
    TestCycleService testCycle,
    DiagnosticStore diagnostics)
{
    [McpServerTool(Name = "run_test_cycle", UseStructuredContent = true, Idempotent = false)]
    [Description("Launch RimWorld with the given mod in an isolated save directory; the user's saves and settings stay untouched. Run build_mod first for C# mods; after launch, read errors with list_test_diagnostics. Requires the game to not be running.")]
    public TestSessionResult RunTestCycle(
        [Description("Directory of the mod to test.")]
        string path,
        [Description("packageIds of additional mods to enable.")]
        string[]? companion_mods = null,
        [Description("Launch with -quicktest to skip the main menu and load a test map directly.")]
        bool quicktest = true) => ToolGuard.Run(() =>
        ToResult(testCycle.Start(path, companion_mods, quicktest)));

    [McpServerTool(Name = "test_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Report the current test session state, including whether the bridge and diagnostics daemon are healthy.")]
    public TestSessionResult TestStatus() => ToolGuard.Run(() => ToResult(testCycle.Status()));

    [McpServerTool(Name = "stop_test", UseStructuredContent = true, Destructive = true)]
    [Description("Stop the test session: remove temporary links, terminate the diagnostics daemon, and clean up the temporary save data.")]
    public TestSessionResult StopTest(
        [Description("Must be explicitly true to proceed.")]
        bool confirm = false,
        [Description("Also terminate the game process; otherwise the user closes the game themselves.")]
        bool terminate_game = false) => ToolGuard.Run(() =>
    {
        if (!confirm)
        {
            // 補一個參數就能重試的情境，訊息必須送達呼叫端。
            throw new ModelContextProtocol.McpException("停止測試會移除連結並終止 daemon，需要 confirm=true。");
        }

        return ToResult(testCycle.Stop(terminate_game));
    });

    [McpServerTool(Name = "list_test_diagnostics", UseStructuredContent = true, ReadOnly = true)]
    [Description("List errors and warnings collected in this test session. Identical entries are merged with an occurrence count.")]
    public ListDiagnosticsResult ListTestDiagnostics(
        [Description("Return only this type: error, warning, diagnostic, loaded_mods, or performance.")]
        string? type = null,
        [Description("Character limit per entry, 100-20000. Use get_test_diagnostic for the full stack trace.")]
        int max_text_length = 2000,
        [Description("Maximum results, 1-500.")]
        int limit = 100) => ToolGuard.Run(() =>
    {
        var records = diagnostics.Read();

        if (!string.IsNullOrEmpty(type))
        {
            records = [.. records.Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))];
        }

        var textLimit = Math.Clamp(max_text_length, 100, 20000);

        // crash loop 可以在幾秒內產生數千筆診斷，一定要有筆數上限——
        // 這是所有查詢型工具裡最容易爆量的一個。
        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var page = records.Take(effectiveLimit).ToList();

        return new ListDiagnosticsResult
        {
            Results = [.. page.Select(r => ToSummary(r, textLimit))],
            Count = page.Count,
            TotalCount = records.Count,
            LimitReached = records.Count > page.Count,
            ErrorCount = records.Count(r => r.Type == "error"),
            WarningCount = records.Count(r => r.Type == "warning"),
        };
    });

    [McpServerTool(Name = "get_test_diagnostic", UseStructuredContent = true, ReadOnly = true)]
    [Description("Fetch one diagnostic in full by hash, including the untruncated stack trace.")]
    public DiagnosticSummary GetTestDiagnostic(
        [Description("The diagnostic hash from list_test_diagnostics.")]
        string diagnostic_hash) => ToolGuard.Run(() =>
    {
        var record = diagnostics.Find(diagnostic_hash)
            ?? throw new KeyNotFoundException($"找不到診斷：{diagnostic_hash}");

        return ToSummary(record, int.MaxValue);
    });

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
        LinksRemaining = [.. session.Links.Select(l => l.Link)],
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

    /// <summary>
    /// 停止後仍未能移除的連結。通常代表遊戲還在執行中佔用著它們——
    /// 關閉遊戲後再呼叫一次 stop_test 即可清乾淨。
    /// </summary>
    [JsonPropertyName("links_remaining")]
    public IReadOnlyList<string> LinksRemaining { get; init; } = [];
}

/// <summary>診斷清單。</summary>
public sealed record ListDiagnosticsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DiagnosticSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>過濾後的總筆數；大於 count 時代表有被 limit 截掉的部分。</summary>
    [JsonPropertyName("total_count")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

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
