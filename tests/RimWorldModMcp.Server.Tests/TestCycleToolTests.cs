using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Server.Tests;

/// <summary>測試工具回傳給 agent 的形狀：以 daemon 寫進 store 的資料為輸入，經完整的 MCP 管線讀出。</summary>
public sealed class TestCycleToolTests : IAsyncLifetime
{
    private McpServerFixture _server = null!;
    private DiagnosticStore _diagnostics = null!;
    private GameStateStore _gameState = null!;

    public async Task InitializeAsync()
    {
        _server = await McpServerFixture.StartAsync();
        _diagnostics = new DiagnosticStore(_server.Store);
        _gameState = new GameStateStore(_server.Store);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public async Task StatusIsIdleWithoutGameStateOnAFreshStore()
    {
        var status = await _server.CallAsync("test_status");

        Assert.Equal("idle", status.GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("game", out _));
    }

    [Fact]
    public async Task StatusReportsTheBridgeGameState()
    {
        _gameState.Write(new GameStateRecord
        {
            ProgramState = "Playing",
            MapLoaded = true,
            Tick = 900,
            Paused = true,
            TimeSpeed = "Paused",
            Loading = false,
            OpenWindows = ["Dialog_MessageBox"],
            Colonists = 3,
            GameVersion = "1.6.4871",
            UptimeMs = 30000,
            At = Now() - 1500,
        });

        var status = await _server.CallAsync("test_status");
        var game = status.GetProperty("game");

        Assert.Equal("Playing", game.GetProperty("program_state").GetString());
        Assert.True(game.GetProperty("map_loaded").GetBoolean());
        Assert.Equal(900, game.GetProperty("tick").GetInt32());
        Assert.True(game.GetProperty("paused").GetBoolean());
        Assert.Equal(["Dialog_MessageBox"], game.GetProperty("open_windows").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(3, game.GetProperty("colonists").GetInt32());
        Assert.InRange(game.GetProperty("age_ms").GetInt64(), 1500, 60000);
    }

    /// <summary>wait_for_state 在狀態到達前持續等待，到達後立刻回來。</summary>
    [Fact]
    public async Task WaitForStateReturnsOnceTheStateIsReached()
    {
        _gameState.Write(new GameStateRecord { ProgramState = "Entry", At = Now() });

        var call = _server.CallAsync("test_status", Args(("wait_for_state", "playing"), ("wait_seconds", 20)));

        await Task.Delay(1200);
        Assert.False(call.IsCompleted);

        _gameState.Write(new GameStateRecord { ProgramState = "Playing", At = Now() });

        var status = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Playing", status.GetProperty("game").GetProperty("program_state").GetString());
    }

    /// <summary>逾時不是錯誤：回目前狀態，讓 agent 自己判斷要不要再等。</summary>
    [Fact]
    public async Task WaitForStateTimesOutWithTheCurrentState()
    {
        _gameState.Write(new GameStateRecord { ProgramState = "Entry", At = Now() });

        var status = await _server.CallAsync("test_status", Args(("wait_for_state", "Playing"), ("wait_seconds", 1)));

        Assert.Equal("Entry", status.GetProperty("game").GetProperty("program_state").GetString());
    }

    [Fact]
    public async Task DiagnosticsListFiltersByTypeButCountsTheWholeSession()
    {
        _diagnostics.Add("error", "NullReferenceException", "NullReferenceException\n  at Verse.Thing.Tick()", "bridge", "run-1");
        _diagnostics.Add("error", "Other failure", "Other failure", "bridge", "run-1");
        _diagnostics.Add("warning", "Slow tick", "Slow tick", "player.log", "run-1");

        var result = await _server.CallAsync("list_test_diagnostics", Args(("type", "warning")));

        Assert.Equal(1, result.GetProperty("count").GetInt32());
        Assert.Equal(1, result.GetProperty("total_count").GetInt32());
        Assert.Equal(2, result.GetProperty("error_count").GetInt32());
        Assert.Equal(1, result.GetProperty("warning_count").GetInt32());
        Assert.False(result.GetProperty("limit_reached").GetBoolean());

        var entry = result.GetProperty("results")[0];
        Assert.Equal("warning", entry.GetProperty("type").GetString());
        Assert.Equal("player.log", entry.GetProperty("source").GetString());

        // 單行診斷不重複送 text：省下的 token 加總起來可觀。
        Assert.False(entry.TryGetProperty("text", out _));
        Assert.False(entry.GetProperty("text_truncated").GetBoolean());
    }

    [Fact]
    public async Task DiagnosticsListTruncatesLongTextAndSaysSo()
    {
        var stack = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"  at Verse.Frame{i}.Tick()"));
        _diagnostics.Add("error", "boom", "boom\n" + stack, "bridge", "run-1");

        var result = await _server.CallAsync("list_test_diagnostics", Args(("max_text_length", 100)));
        var entry = result.GetProperty("results")[0];

        Assert.True(entry.GetProperty("text_truncated").GetBoolean());
        Assert.Equal(100, entry.GetProperty("text").GetString()!.Length);

        var full = await _server.CallAsync("get_test_diagnostic", Args(("diagnostic_hash", entry.GetProperty("hash").GetString())));

        Assert.False(full.GetProperty("text_truncated").GetBoolean());
        Assert.EndsWith("Frame39.Tick()", full.GetProperty("text").GetString());
    }

    /// <summary>since_at 是輪詢游標：拿上一次的 latest_at 回傳，只會看到之後的新東西。</summary>
    [Fact]
    public async Task SinceAtCursorOnlyReturnsNewerEntries()
    {
        _diagnostics.Add("error", "first", "first", "bridge", "run-1");

        var initial = await _server.CallAsync("list_test_diagnostics");
        var cursor = initial.GetProperty("latest_at").GetInt64();
        Assert.Equal(1, initial.GetProperty("count").GetInt32());

        var nothing = await _server.CallAsync("list_test_diagnostics", Args(("since_at", cursor)));
        Assert.Equal(0, nothing.GetProperty("count").GetInt32());
        Assert.Equal(cursor, nothing.GetProperty("latest_at").GetInt64());

        await Task.Delay(5); // 讓新紀錄的時間戳大於游標。
        _diagnostics.Add("error", "second", "second", "bridge", "run-1");

        var newer = await _server.CallAsync("list_test_diagnostics", Args(("since_at", cursor)));
        Assert.Equal(1, newer.GetProperty("count").GetInt32());
        Assert.Equal("second", newer.GetProperty("results")[0].GetProperty("first_line").GetString());
    }

    /// <summary>
    /// 一次冒出超過 limit 筆時，游標只能推進到本頁最後一筆；之前取的是全部的最大值，
    /// 被截掉的下一輪就永遠拿不到。crash loop 幾秒內就會超過預設的 50 筆。
    /// </summary>
    [Fact]
    public async Task CursorNeverSkipsEntriesCutOffByTheLimit()
    {
        for (var i = 0; i < 5; i++)
        {
            _diagnostics.Add("error", $"e{i}", $"e{i}", "bridge", "run-1");
            await Task.Delay(3); // 時間戳遞增，頁面切割才有意義。
        }

        var seen = new List<string>();
        long cursor = 0;

        for (var page = 0; page < 5; page++)
        {
            var result = await _server.CallAsync("list_test_diagnostics", Args(("limit", 2), ("since_at", cursor)));
            seen.AddRange(result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("first_line").GetString()!));
            cursor = result.GetProperty("latest_at").GetInt64();

            if (result.GetProperty("count").GetInt32() == 0)
            {
                break;
            }
        }

        Assert.Equal(["e0", "e1", "e2", "e3", "e4"], seen);
    }

    [Fact]
    public async Task StopTestRequiresConfirmation()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _server.CallAsync("stop_test"));

        Assert.Contains("confirm=true", error.Message);
    }
}
