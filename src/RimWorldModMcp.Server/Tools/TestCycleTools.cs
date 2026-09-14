using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Server.Tools;

#pragma warning disable IDE1006 // 參數名刻意使用 snake_case，見 IndexTools 的說明。

/// <summary>Tools for testing mods in an isolated session and reading diagnostics.</summary>
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
        bool quicktest = true,
        [Description("Config files to copy into the isolated session's Config directory before launch, e.g. the mod's saved ModSettings. Files named Mod_<folder>_<class>.xml are renamed to match the test session's mod folder so RimWorld picks them up. ModsConfig.xml is rejected.")]
        string[]? seed_config = null,
        [Description("Launch as a borderless fullscreen window (rewrites <fullscreen> in the isolated Prefs.xml copy; the user's own Prefs.xml is untouched). Set false to keep whatever the user's Prefs.xml says.")]
        bool fullscreen = true) => ToolGuard.Run(() =>
        ToResult(testCycle.Start(path, companion_mods, quicktest, seed_config, fullscreen)));

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
            throw new ModelContextProtocol.McpException("Stopping the test removes the links and terminates the daemon; confirm=true is required.");
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
            ?? throw new KeyNotFoundException($"Diagnostic not found: {diagnostic_hash}");

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

/// <summary>Test session state.</summary>
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

    /// <summary>Soft load-after targets absent from the selection, hence skipped.</summary>
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

    /// <summary><c>started</c>, <c>reused</c>, or <c>unavailable</c>.</summary>
    [JsonPropertyName("daemon_state")]
    public string? DaemonState { get; init; }

    /// <summary>Why the daemon is unavailable. Bridge diagnostics fall back to Player.log when set.</summary>
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
    /// Links that could not be removed after stopping. Usually the game still holds them —
    /// close the game and call stop_test again to finish cleanup.
    /// </summary>
    [JsonPropertyName("links_remaining")]
    public IReadOnlyList<string> LinksRemaining { get; init; } = [];
}

/// <summary>Diagnostic listing.</summary>
public sealed record ListDiagnosticsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DiagnosticSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Filtered total; above count means limit cut some off.</summary>
    [JsonPropertyName("total_count")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    [JsonPropertyName("error_count")]
    public required int ErrorCount { get; init; }

    [JsonPropertyName("warning_count")]
    public required int WarningCount { get; init; }
}

/// <summary>One diagnostic.</summary>
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

    /// <summary><c>bridge</c> or <c>player.log</c>.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    /// <summary>Occurrence count of this diagnostic in this session.</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("at")]
    public required long At { get; init; }
}
