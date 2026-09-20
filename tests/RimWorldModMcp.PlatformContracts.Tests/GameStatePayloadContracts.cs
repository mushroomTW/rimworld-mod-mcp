using System.Text.Json;
using RimWorldModMcp.Bridge;

namespace RimWorldModMcp.PlatformContracts.Tests;

/// <summary>
/// Bridge 送出的 game_state 行格式契約。與 <see cref="DiagnosticPayloadContracts"/>
/// 相同的做法：以原始碼連結引入 GameStatePayload.cs，在行程內驗證。
/// </summary>
public sealed class GameStatePayloadContracts
{
    [Fact]
    public void OutputIsValidJsonWithTypedFields()
    {
        var line = GameStatePayload.BuildLine("tok", new GameStateSnapshot
        {
            ProgramState = "Playing",
            MapLoaded = true,
            Tick = 4200,
            Paused = false,
            TimeSpeed = "Fast",
            Loading = false,
            OpenWindows = ["MainTabWindow_Inspect", "Dialog_MessageBox"],
            Colonists = 5,
            GameVersion = "1.6.4871 rev590",
            UptimeMs = 12345,
        });

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("game_state", root.GetProperty("type").GetString());
        Assert.Equal("tok", root.GetProperty("token").GetString());
        Assert.Equal("Playing", root.GetProperty("program_state").GetString());
        Assert.True(root.GetProperty("map_loaded").GetBoolean());
        Assert.Equal(4200, root.GetProperty("tick").GetInt32());
        Assert.False(root.GetProperty("paused").GetBoolean());
        Assert.Equal("Fast", root.GetProperty("time_speed").GetString());
        Assert.False(root.GetProperty("loading").GetBoolean());
        Assert.Equal(["MainTabWindow_Inspect", "Dialog_MessageBox"], root.GetProperty("open_windows").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(5, root.GetProperty("colonists").GetInt32());
        Assert.Equal("1.6.4871 rev590", root.GetProperty("game_version").GetString());
        Assert.Equal(12345, root.GetProperty("uptime_ms").GetInt64());
    }

    /// <summary>視窗型別名稱來自反射，理論上可控；但轉義仍要走同一條路，注入不能偽造欄位。</summary>
    [Fact]
    public void StringsAreEscaped()
    {
        var line = GameStatePayload.BuildLine("t", new GameStateSnapshot
        {
            ProgramState = "Entry",
            OpenWindows = ["\"}{\"type\":\"injected"],
        });

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("game_state", root.GetProperty("type").GetString());
        Assert.Equal("\"}{\"type\":\"injected", root.GetProperty("open_windows")[0].GetString());
    }

    [Fact]
    public void EmptyWindowsProduceEmptyArray()
    {
        var line = GameStatePayload.BuildLine("t", new GameStateSnapshot { ProgramState = "Entry" });

        using var document = JsonDocument.Parse(line);

        Assert.Equal(0, document.RootElement.GetProperty("open_windows").GetArrayLength());
    }
}
