using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>一個測試場次的狀態。</summary>
public sealed record TestSession
{
    /// <summary>idle、starting、running、needs_cleanup 或 stopped。needs_cleanup 代表仍有存活行程或殘留資源待清理，可再次 stop_test。</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("started_at")]
    public long? StartedAt { get; init; }

    [JsonPropertyName("mod")]
    public string? Mod { get; init; }

    [JsonPropertyName("save_data")]
    public string? SaveData { get; init; }

    [JsonPropertyName("active_mods")]
    public IReadOnlyList<string> ActiveMods { get; init; } = [];

    [JsonPropertyName("skipped_load_after")]
    public IReadOnlyList<string> SkippedLoadAfter { get; init; } = [];

    [JsonPropertyName("links")]
    public IReadOnlyList<TestLink> Links { get; init; } = [];

    [JsonPropertyName("player_log")]
    public string? PlayerLog { get; init; }

    /// <summary>啟動遊戲前 Player.log 的大小，用來只讀取這次測試新增的內容。</summary>
    [JsonPropertyName("log_offset")]
    public long LogOffset { get; init; }

    [JsonPropertyName("bridge_port")]
    public int BridgePort { get; init; }

    [JsonPropertyName("bridge")]
    public BridgeState? Bridge { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonState? Daemon { get; init; }

    /// <summary>只有在本輪由自己啟動 daemon 時才有值，避免停止時誤殺別人的 daemon。</summary>
    [JsonPropertyName("daemon_pid")]
    public int? DaemonPid { get; init; }

    /// <summary>daemon 行程的啟動時間。終止前比對，防 PID 重用誤殺無關行程。</summary>
    [JsonPropertyName("daemon_start_utc")]
    public DateTime? DaemonStartUtc { get; init; }

    [JsonPropertyName("game_pid")]
    public int? GamePid { get; init; }

    /// <summary>遊戲行程的啟動時間。終止前比對，防 PID 重用誤殺無關行程。</summary>
    [JsonPropertyName("game_start_utc")]
    public DateTime? GameStartUtc { get; init; }

    [JsonPropertyName("previous_run")]
    public string? PreviousRun { get; init; }

    [JsonPropertyName("terminated")]
    public TerminationResult? Terminated { get; init; }

    public static TestSession Idle { get; } = new() { State = "idle" };
}

/// <summary>一條建立起來的目錄連結。</summary>
public sealed record TestLink(
    [property: JsonPropertyName("link")] string Link,
    [property: JsonPropertyName("target")] string Target);

/// <summary>Bridge 的可用狀態。</summary>
public sealed record BridgeState
{
    /// <summary>active 或 unavailable。</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("package_id")]
    public string? PackageId { get; init; }

    /// <summary>prebuilt（工具隨附、依遊戲版本直接使用）或 built（就地以本機遊戲組件建置）。</summary>
    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>診斷 daemon 的狀態。</summary>
public sealed record DaemonState
{
    /// <summary>started（本輪啟動）、reused（沿用既有）或 unavailable。</summary>
    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("owner_pid")]
    public int? OwnerPid { get; init; }

    /// <summary>
    /// 不可用的原因。這個欄位必須被回報給使用者——
    /// 埠被別的程序佔住時整個 Bridge 診斷會失效，只剩 Player.log 可用，
    /// 靜默降級會讓人以為 Mod 沒有錯誤。
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>停止測試時的終止結果。</summary>
public sealed record TerminationResult
{
    [JsonPropertyName("daemon")]
    public bool Daemon { get; init; }

    [JsonPropertyName("game")]
    public bool Game { get; init; }
}

/// <summary>測試場次狀態的持久化。</summary>
public sealed class TestSessionStore(StoreDirectories store)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TestSession Read()
    {
        try
        {
            return JsonSerializer.Deserialize<TestSession>(File.ReadAllText(store.TestStatusFile)) ?? TestSession.Idle;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return TestSession.Idle;
        }
    }

    public void Write(TestSession session) => AtomicJson.Write(store.TestStatusFile, session, JsonOptions);
}

/// <summary>daemon 的自述檔，讓其他程序能認出「這個埠是我們的 daemon 佔的」。</summary>
public sealed record DaemonRecord
{
    /// <summary>完整模式：Bridge＋Player.log 皆可用。</summary>
    public const string ModeFull = "full";

    /// <summary>僅日誌模式：埠被佔用，僅 Player.log 可用。</summary>
    public const string ModeLogOnly = "log_only";

    [JsonPropertyName("pid")]
    public required int Pid { get; init; }

    [JsonPropertyName("port")]
    public required int Port { get; init; }

    [JsonPropertyName("started_at")]
    public required long StartedAt { get; init; }

    [JsonPropertyName("start_time_utc")]
    public DateTime? StartTimeUtc { get; init; }

    /// <summary>full（Bridge + Player.log）或 log_only（埠被佔用，僅 Player.log）。</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>log_only 時說明 Bridge 不可用的原因。</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>daemon 自述檔的讀寫。</summary>
public sealed class DaemonRecordStore(StoreDirectories store)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DaemonRecord? Read()
    {
        try
        {
            return JsonSerializer.Deserialize<DaemonRecord>(File.ReadAllText(store.DaemonFile));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Write(DaemonRecord record) => AtomicJson.Write(store.DaemonFile, record, JsonOptions);

    /// <summary>只在自述檔確實屬於指定 PID 時才清除，避免誤刪後繼 daemon 的紀錄。</summary>
    public void ClearIfOwnedBy(int pid)
    {
        if (Read()?.Pid != pid)
        {
            return;
        }

        try
        {
            File.Delete(store.DaemonFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 檔案可能已被其他行程刪除或鎖定，盡力清理即可，不阻礙主流程。
        }
    }
}
