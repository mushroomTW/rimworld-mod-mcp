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
    DiagnosticStore diagnostics,
    GameStateStore gameState)
{
    [McpServerTool(Name = "run_test_cycle", UseStructuredContent = true, Idempotent = false)]
    [Description("Launch RimWorld with the given mod in an isolated save directory; the user's saves and settings stay untouched. Run build_mod first for C# mods; after launch, read errors with list_test_diagnostics. Requires the game to not be running.")]
    public TestSessionResult RunTestCycle(
        [Description("Directory of the mod to test.")]
        string path,
        [Description("packageIds of installed mods to enable alongside, e.g. the mod you are patching or checking compatibility with.")]
        string[]? companion_mods = null,
        [Description("Launch with -quicktest to skip the main menu and load a test map directly.")]
        bool quicktest = true,
        [Description("Config files to copy into the isolated session's Config directory before launch, e.g. the mod's saved ModSettings. Files named Mod_<folder>_<class>.xml are renamed to match the test session's mod folder so RimWorld picks them up. ModsConfig.xml is rejected.")]
        string[]? seed_config = null,
        [Description("Launch as a borderless fullscreen window (rewrites <fullscreen> in the isolated Prefs.xml copy; the user's own Prefs.xml is untouched). Set false to keep whatever the user's Prefs.xml says.")]
        bool fullscreen = true) => ToolGuard.Run(() =>
        ToResult(testCycle.Start(path, companion_mods, quicktest, seed_config, fullscreen), game: null));

    [McpServerTool(Name = "test_status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Report the current test session state: whether the bridge and diagnostics daemon are healthy, and game (the in-game state the bridge last reported: program_state Entry/MapInitializing/Playing, map_loaded, tick, paused, loading, open_windows, age_ms). game is null until the bridge reports. Pass wait_for_state to block until program_state reaches it, e.g. wait_for_state=Playing before checking for errors.")]
    public Task<TestSessionResult> TestStatus(
        [Description("Long-poll until game.program_state equals this value (Entry, MapInitializing, or Playing; case-insensitive). Returns the current state when wait_seconds elapses first.")]
        string? wait_for_state = null,
        [Description("How long wait_for_state may block, 0-50 seconds. Keep it below your client's tool-call timeout.")]
        int wait_seconds = 30,
        CancellationToken cancellationToken = default) => ToolGuard.RunAsync(async () =>
    {
        var state = gameState.Read();

        if (!string.IsNullOrWhiteSpace(wait_for_state))
        {
            // 與 list_test_diagnostics 相同的理由：daemon 是另一個行程寫檔，只能輪詢，
            // 但把輪詢留在 server 端，agent 的一次呼叫就抵過原本十次「到了沒」。
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait_seconds, 0, 50));

            while (!Reached(state, wait_for_state) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                state = gameState.Read();
            }
        }

        return ToResult(testCycle.Status(), state);
    });

    private static bool Reached(GameStateRecord? state, string target)
        => state is not null && string.Equals(state.ProgramState, target.Trim(), StringComparison.OrdinalIgnoreCase);

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

        return ToResult(testCycle.Stop(terminate_game), game: null);
    });

    [McpServerTool(Name = "list_test_diagnostics", UseStructuredContent = true, ReadOnly = true)]
    [Description("List errors and warnings collected in this test session. Identical entries are merged with an occurrence count. To poll: pass the previous result's latest_at as since_at and a wait_seconds so the call blocks until something new arrives instead of re-reading the whole list.")]
    public Task<ListDiagnosticsResult> ListTestDiagnostics(
        [Description("Return only this type: error, warning, diagnostic, loaded_mods, or performance.")]
        string? type = null,
        [Description("Character limit per entry, 100-20000; the default keeps the message and the top frames. Use get_test_diagnostic for the full stack trace.")]
        int max_text_length = 600,
        [Description("Maximum results, 1-500.")]
        int limit = 50,
        [Description("Only entries new or re-occurring after this Unix-millisecond timestamp (use latest_at from the previous call). 0 returns everything.")]
        long since_at = 0,
        [Description("Long-poll: when nothing matches, keep waiting up to this many seconds (0-50) for new entries before returning. Keep it below your client's tool-call timeout.")]
        int wait_seconds = 0,
        CancellationToken cancellationToken = default) => ToolGuard.RunAsync(async () =>
    {
        // 上限 50 秒：常見 MCP client 的單次工具呼叫逾時約 60 秒，超過的話 client 先報錯、
        // server 還在等，agent 看到的是工具壞掉而不是空結果。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(wait_seconds, 0, 50));
        var records = Filter(diagnostics.ReadSince(since_at), type);

        // 沒有新東西就等：daemon 是另一個行程寫檔，這裡只能輪詢，但把輪詢
        // 留在 server 端，agent 的一次呼叫就抵過原本十次「問了又沒有」。
        while (records.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            records = Filter(diagnostics.ReadSince(since_at), type);
        }

        var textLimit = Math.Clamp(max_text_length, 100, 20000);

        // crash loop 可以在幾秒內產生數千筆診斷，一定要有筆數上限——
        // 這是所有查詢型工具裡最容易爆量的一個。
        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var page = records.Take(effectiveLimit).ToList();

        // error_count / warning_count 永遠是整個場次的總數，不受 since_at 影響——
        // 輪詢中的 agent 看到 error_count=0 會直接下「測試無錯誤」的結論。
        var all = diagnostics.Read();

        return new ListDiagnosticsResult
        {
            Results = [.. page.Select(r => ToSummary(r, textLimit))],
            Count = page.Count,
            TotalCount = records.Count,
            LimitReached = records.Count > page.Count,
            ErrorCount = all.Count(r => r.Type == "error"),
            WarningCount = all.Count(r => r.Type == "warning"),
            LatestAt = records.Count == 0 ? since_at : records.Max(r => r.At),
        };
    });

    private static IReadOnlyList<DiagnosticRecord> Filter(IReadOnlyList<DiagnosticRecord> records, string? type)
        => string.IsNullOrEmpty(type)
            ? records
            : [.. records.Where(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))];

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

    private static TestSessionResult ToResult(TestSession session, GameStateRecord? game) => new()
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
        BridgeOrigin = session.Bridge?.Origin,
        BridgeReason = session.Bridge?.Reason,
        DaemonState = session.Daemon?.State,
        DaemonReason = session.Daemon?.Reason,
        GamePid = session.GamePid,
        PreviousRun = session.PreviousRun,
        TerminatedDaemon = session.Terminated?.Daemon,
        TerminatedGame = session.Terminated?.Game,
        LinksRemaining = [.. session.Links.Select(l => l.Link)],
        Game = game is null ? null : new GameStateSummary
        {
            ProgramState = game.ProgramState,
            MapLoaded = game.MapLoaded,
            Tick = game.Tick,
            Paused = game.Paused,
            TimeSpeed = game.TimeSpeed,
            Loading = game.Loading,
            OpenWindows = game.OpenWindows,
            Colonists = game.Colonists,
            GameVersion = game.GameVersion,
            UptimeMs = game.UptimeMs,
            AgeMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - game.At),
        },
    };

    private static DiagnosticSummary ToSummary(DiagnosticRecord record, int maxTextLength) => new()
    {
        Hash = record.Hash,
        Type = record.Type,
        FirstLine = record.FirstLine,
        // 單行診斷的 text 就是 first_line，重複送一次沒有意義。
        Text = record.Text == record.FirstLine ? null
            : record.Text.Length > maxTextLength ? record.Text[..maxTextLength]
            : record.Text,
        TextTruncated = record.Text != record.FirstLine && record.Text.Length > maxTextLength,
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

    /// <summary><c>prebuilt</c> (shipped with the tool, no SDK needed) or <c>built</c> (compiled locally against the installed game).</summary>
    [JsonPropertyName("bridge_origin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BridgeOrigin { get; init; }

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

    /// <summary>In-game state last reported by the bridge; null until the bridge connects. Only test_status fills it.</summary>
    [JsonPropertyName("game")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GameStateSummary? Game { get; init; }
}

/// <summary>In-game state as reported by the bridge.</summary>
public sealed record GameStateSummary
{
    /// <summary><c>Entry</c> (main menu), <c>MapInitializing</c>, or <c>Playing</c>.</summary>
    [JsonPropertyName("program_state")]
    public required string ProgramState { get; init; }

    [JsonPropertyName("map_loaded")]
    public required bool MapLoaded { get; init; }

    [JsonPropertyName("tick")]
    public required int Tick { get; init; }

    [JsonPropertyName("paused")]
    public required bool Paused { get; init; }

    [JsonPropertyName("time_speed")]
    public string? TimeSpeed { get; init; }

    /// <summary>A long operation (loading, map generation) is running or queued.</summary>
    [JsonPropertyName("loading")]
    public required bool Loading { get; init; }

    /// <summary>Open window type names, bottom to top. A Dialog_* here usually means something is blocking the game.</summary>
    [JsonPropertyName("open_windows")]
    public IReadOnlyList<string> OpenWindows { get; init; } = [];

    [JsonPropertyName("colonists")]
    public required int Colonists { get; init; }

    [JsonPropertyName("game_version")]
    public string? GameVersion { get; init; }

    [JsonPropertyName("uptime_ms")]
    public required long UptimeMs { get; init; }

    /// <summary>Milliseconds since this report arrived. The bridge sends at least every 5 seconds; a much larger value means the game stopped responding.</summary>
    [JsonPropertyName("age_ms")]
    public required long AgeMs { get; init; }
}

/// <summary>Diagnostic listing.</summary>
public sealed record ListDiagnosticsResult
{
    [JsonPropertyName("results")]
    public required IReadOnlyList<DiagnosticSummary> Results { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Total matching type and since_at; above count means limit cut some off.</summary>
    [JsonPropertyName("total_count")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("limit_reached")]
    public required bool LimitReached { get; init; }

    /// <summary>Whole-session error count, regardless of type and since_at.</summary>
    [JsonPropertyName("error_count")]
    public required int ErrorCount { get; init; }

    /// <summary>Whole-session warning count, regardless of type and since_at.</summary>
    [JsonPropertyName("warning_count")]
    public required int WarningCount { get; init; }

    /// <summary>Cursor for the next poll: pass it back as since_at. Equals since_at when nothing was returned.</summary>
    [JsonPropertyName("latest_at")]
    public required long LatestAt { get; init; }
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

    /// <summary>Full text, possibly truncated. Omitted when the diagnostic is a single line (identical to first_line).</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

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
