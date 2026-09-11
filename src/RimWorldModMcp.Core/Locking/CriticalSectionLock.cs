using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core.Locking;

/// <summary>鎖檔內容。</summary>
public sealed record LockRecord
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("pid")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("created_at")]
    public required long CreatedAt { get; init; }

    /// <summary>
    /// 持有者程序的啟動時間。配合 PID 一起比對才能認出「同一個程序」——
    /// 單看 PID 會在作業系統重用 PID 時把新程序誤認成舊持有者。
    /// Python 版沒有這層防護。
    /// </summary>
    [JsonPropertyName("start_time_utc")]
    public DateTime? StartTimeUtc { get; init; }

    [JsonPropertyName("details")]
    public Dictionary<string, string>? Details { get; init; }
}

/// <summary>
/// 跨程序的臨界區鎖。
///
/// <para>
/// 不是 OS 層的檔案鎖，而是「原子建檔 + 持有者存活性檢查」的 advisory lock。
/// 原子性來自 <see cref="FileMode.CreateNew"/>：同一時間只有一個程序能成功建立鎖檔。
/// </para>
/// <para>
/// <b>使用範圍的紀律</b>：這個鎖只保護短暫的臨界區（例如「建連結→寫設定→啟動遊戲」）。
/// 長時間的狀態（測試場次進行中）改由狀態檔維持，否則忘記停止的場次會把鎖
/// 留在長壽的 MCP server 程序名下，殘骸回收永遠不會觸發。
/// </para>
/// </summary>
public sealed class CriticalSectionLock(StoreDirectories store, IProcessHost processes)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>取得鎖，回傳可用於釋放的 token。取不到時拋出。</summary>
    public string Acquire(string name, IReadOnlyDictionary<string, string>? details = null)
    {
        var path = LockPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // 只允許接管殘骸鎖一次：第一輪發現持有者已死就刪掉重試，
        // 第二輪若仍失敗就是真的有人持有（或有另一個程序同時接管成功）。
        foreach (var mayReclaim in (ReadOnlySpan<bool>)[true, false])
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                var record = new LockRecord
                {
                    Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant(),
                    ProcessId = Environment.ProcessId,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    StartTimeUtc = processes.StartTimeUtc(Environment.ProcessId),
                    Details = details?.ToDictionary(kv => kv.Key, kv => kv.Value),
                };

                JsonSerializer.Serialize(stream, record, JsonOptions);
                return record.Token;
            }
            catch (IOException) when (File.Exists(path))
            {
                var (outcome, existing) = ReadClassified(path);

                // 「讀不到」不等於「殘骸」。持有者以 FileShare.None 建檔後、寫完內容前，
                // 我們的讀取會吃到分享違規——那是一個活得好好的鎖，不能因此刪掉它，
                // 否則兩個程序會同時認為自己持有臨界區。短暫重試後仍讀不到就當作被持有。
                for (var attempt = 0; outcome == ReadOutcome.Unreadable && attempt < 5; attempt++)
                {
                    Thread.Sleep(50);
                    (outcome, existing) = ReadClassified(path);
                }

                var stale = outcome switch
                {
                    // 內容壞掉的鎖檔視為殘骸——留著它只會讓工具永遠卡住。
                    ReadOutcome.Corrupt => true,
                    ReadOutcome.Ok => IsStale(existing),
                    // 檔案在讀取前消失：持有者剛釋放，直接重試建檔。
                    ReadOutcome.Missing => true,
                    _ => false,
                };

                if (mayReclaim && stale)
                {
                    TryDelete(path);
                    continue;
                }

                var holder = existing?.ProcessId.ToString() ?? "未知";
                throw new InvalidOperationException($"已有進行中的 {name} 工作（持有者 PID {holder}）。");
            }
        }

        throw new InvalidOperationException($"已有進行中的 {name} 工作。");
    }

    /// <summary>釋放鎖。token 不符時什麼都不做——避免誤刪別人接管後的鎖。</summary>
    public void Release(string name, string token)
    {
        var path = LockPath(name);
        var existing = Read(path);

        if (existing is not null && string.Equals(existing.Token, token, StringComparison.Ordinal))
        {
            TryDelete(path);
        }
    }

    /// <summary>以 <c>using</c> 持有鎖，離開範圍時自動釋放。</summary>
    public IDisposable Hold(string name, IReadOnlyDictionary<string, string>? details = null)
    {
        var token = Acquire(name, details);
        return new Holder(this, name, token);
    }

    /// <summary>持有者是否已經不存在（可以接管）。</summary>
    private bool IsStale(LockRecord? record)
    {
        if (record is null || record.ProcessId <= 0)
        {
            return true;
        }

        if (!processes.IsAlive(record.ProcessId))
        {
            return true;
        }

        // PID 還活著，但如果啟動時間對不上，代表這個 PID 已經被重用給另一個程序，
        // 原持有者其實已經結束。兩邊都拿得到啟動時間時才做這個判斷。
        var recordedStart = record.StartTimeUtc;
        var actualStart = processes.StartTimeUtc(record.ProcessId);

        if (recordedStart is not null && actualStart is not null)
        {
            // 檔案往返會損失精度，容忍一秒內的差異。
            return Math.Abs((recordedStart.Value - actualStart.Value).TotalSeconds) > 1;
        }

        return false;
    }

    private string LockPath(string name) => Path.Combine(store.LocksDirectory, name + ".json");

    private enum ReadOutcome
    {
        Ok,

        /// <summary>檔案不存在。</summary>
        Missing,

        /// <summary>存在但目前讀不到（典型：持有者仍握著 FileShare.None 在寫入）。</summary>
        Unreadable,

        /// <summary>讀得到但內容不是合法的鎖紀錄。</summary>
        Corrupt,
    }

    private static (ReadOutcome Outcome, LockRecord? Record) ReadClassified(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<LockRecord>(File.ReadAllText(path));
            return record is null ? (ReadOutcome.Corrupt, null) : (ReadOutcome.Ok, record);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ReadOutcome.Missing, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (ReadOutcome.Unreadable, null);
        }
        catch (JsonException)
        {
            return (ReadOutcome.Corrupt, null);
        }
    }

    private static LockRecord? Read(string path) => ReadClassified(path).Record;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Holder(CriticalSectionLock owner, string name, string token) : IDisposable
    {
        public void Dispose() => owner.Release(name, token);
    }
}
