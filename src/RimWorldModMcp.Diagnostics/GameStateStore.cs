using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>Bridge 回報的一份遊戲狀態。</summary>
public sealed record GameStateRecord
{
    /// <summary>RimWorld 的 ProgramState：Entry、MapInitializing 或 Playing。</summary>
    [JsonPropertyName("program_state")]
    public required string ProgramState { get; init; }

    [JsonPropertyName("map_loaded")]
    public bool MapLoaded { get; init; }

    [JsonPropertyName("tick")]
    public int Tick { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }

    [JsonPropertyName("time_speed")]
    public string? TimeSpeed { get; init; }

    /// <summary>是否有長時間作業（載入、產生地圖）進行中或排隊。</summary>
    [JsonPropertyName("loading")]
    public bool Loading { get; init; }

    /// <summary>目前開啟的視窗型別名稱，由底到頂；用來判斷有沒有對話框擋住流程。</summary>
    [JsonPropertyName("open_windows")]
    public IReadOnlyList<string> OpenWindows { get; init; } = [];

    [JsonPropertyName("colonists")]
    public int Colonists { get; init; }

    [JsonPropertyName("game_version")]
    public string? GameVersion { get; init; }

    /// <summary>Bridge 啟動後經過的毫秒數。</summary>
    [JsonPropertyName("uptime_ms")]
    public long UptimeMs { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    /// <summary>daemon 收到這份狀態的 Unix 毫秒時間。</summary>
    [JsonPropertyName("at")]
    public required long At { get; init; }
}

/// <summary>
/// 遊戲狀態的持久化：daemon 寫、server 讀，永遠只有最新一份。
///
/// <para>
/// 不像 <see cref="DiagnosticStore"/> 需要讀改寫，這裡是整份覆蓋，
/// 所以只靠 <see cref="AtomicJson"/> 的暫存檔加改名就能保證讀端不會看到半份。
/// </para>
/// </summary>
public sealed class GameStateStore(StoreDirectories store)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Write(GameStateRecord record) => AtomicJson.Write(store.GameStateFile, record, JsonOptions);

    /// <summary>沒有回報過、或檔案正在被覆寫時回 null。</summary>
    public GameStateRecord? Read() => AtomicJson.Read<GameStateRecord>(store.GameStateFile, JsonOptions);

    /// <summary>場次開始與結束時清掉，舊場次的狀態不能被誤認成這一場的。</summary>
    public void Clear()
    {
        try
        {
            File.Delete(store.GameStateFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 檔案被讀端短暫佔住時略過；下一筆回報會直接覆蓋。
        }
    }
}
