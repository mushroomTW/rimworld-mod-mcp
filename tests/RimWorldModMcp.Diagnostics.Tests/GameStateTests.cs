using System.Text.Json;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>game_state 訊息的接收：走獨立的儲存，不混進診斷清單。</summary>
public sealed class GameStateTests : IDisposable
{
    private const string Token = "the-real-token";

    private readonly string _root;
    private readonly DiagnosticStore _diagnostics;
    private readonly GameStateStore _gameState;
    private readonly TestSessionStore _sessions;
    private readonly DaemonListener _listener;

    public GameStateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-gamestate-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _diagnostics = new DiagnosticStore(store);
        _gameState = new GameStateStore(store);
        _sessions = new TestSessionStore(store);
        _listener = new DaemonListener(store, RimWorldLocator.DefaultBridgePort, _diagnostics, _sessions, new DaemonRecordStore(store), _gameState);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Line(string token, object extra)
    {
        var payload = new Dictionary<string, object?> { ["type"] = "game_state", ["token"] = token };

        foreach (var property in extra.GetType().GetProperties())
        {
            payload[property.Name] = property.GetValue(extra);
        }

        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void GameStateIsStoredSeparatelyFromDiagnostics()
    {
        _sessions.Write(new TestSession { State = "running", RunId = "run-1" });

        _listener.Accept(Line(Token, new
        {
            program_state = "Playing",
            map_loaded = true,
            tick = 1234,
            paused = true,
            time_speed = "Paused",
            loading = false,
            open_windows = new[] { "MainTabWindow_Inspect", "Dialog_MessageBox" },
            colonists = 3,
            game_version = "1.6.4871",
            uptime_ms = 45000L,
        }), Token);

        var state = _gameState.Read();

        Assert.NotNull(state);
        Assert.Equal("Playing", state.ProgramState);
        Assert.True(state.MapLoaded);
        Assert.Equal(1234, state.Tick);
        Assert.True(state.Paused);
        Assert.Equal("Paused", state.TimeSpeed);
        Assert.Equal(["MainTabWindow_Inspect", "Dialog_MessageBox"], state.OpenWindows);
        Assert.Equal(3, state.Colonists);
        Assert.Equal("1.6.4871", state.GameVersion);
        Assert.Equal(45000L, state.UptimeMs);
        Assert.Equal("run-1", state.RunId);
        Assert.True(state.At > 0);

        // 狀態不是診斷：不能出現在 list_test_diagnostics 的清單裡。
        Assert.Empty(_diagnostics.Read());
    }

    /// <summary>只保留最新一份；舊的直接被覆蓋。</summary>
    [Fact]
    public void LatestReportWins()
    {
        _listener.Accept(Line(Token, new { program_state = "Entry" }), Token);
        _listener.Accept(Line(Token, new { program_state = "Playing", tick = 10 }), Token);

        var state = _gameState.Read();

        Assert.NotNull(state);
        Assert.Equal("Playing", state.ProgramState);
        Assert.Equal(10, state.Tick);
    }

    /// <summary>token 驗證對 game_state 一樣生效。</summary>
    [Fact]
    public void WrongTokenIsRejected()
    {
        _listener.Accept(Line("forged", new { program_state = "Playing" }), Token);

        Assert.Null(_gameState.Read());
    }

    /// <summary>欄位缺漏或型別錯誤時各自退回預設值，其餘欄位照常。</summary>
    [Fact]
    public void MissingOrMistypedFieldsFallBackToDefaults()
    {
        _listener.Accept(Line(Token, new { program_state = "Entry", tick = "not-a-number", open_windows = "not-an-array" }), Token);

        var state = _gameState.Read();

        Assert.NotNull(state);
        Assert.Equal("Entry", state.ProgramState);
        Assert.Equal(0, state.Tick);
        Assert.Empty(state.OpenWindows);
        Assert.False(state.MapLoaded);
    }

    [Fact]
    public void ClearRemovesTheReport()
    {
        _listener.Accept(Line(Token, new { program_state = "Playing" }), Token);
        Assert.NotNull(_gameState.Read());

        _gameState.Clear();

        Assert.Null(_gameState.Read());
        _gameState.Clear(); // 已經不存在時也不能拋。
    }
}
