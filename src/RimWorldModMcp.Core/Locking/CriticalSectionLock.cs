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
/// 鎖被其他程序（或同一程序的其他工作）持有時拋出。
///
/// <para>
/// 繼承 <see cref="InvalidOperationException"/> 是刻意的：工具層把它當成
/// 「預期內的領域失敗」轉成可讀訊息，既有的行為不變。有自己的型別是為了讓
/// 「等一段時間再試」的呼叫端能精確分辨「鎖被佔住」與其他無關的
/// <see cref="InvalidOperationException"/>。
/// </para>
/// </summary>
public sealed class LockHeldException(string name, int? holderProcessId)
    : InvalidOperationException($"A {name} job is already in progress (holder PID {holderProcessId?.ToString() ?? "unknown"}).")
{
    /// <summary>鎖的名稱。</summary>
    public string LockName { get; } = name;

    /// <summary>持有者的 PID；讀不到鎖檔內容時為 null。</summary>
    public int? HolderProcessId { get; } = holderProcessId;
}

/// <summary>
/// 跨程序的臨界區鎖。
///
/// <para>
/// 不是 OS 層的檔案鎖，而是「原子建檔 + 持有者存活性檢查」的 advisory lock。
/// 原子性來自 <see cref="FileMode.CreateNew"/>：同一時間只有一個程序能成功建立鎖檔。
/// </para>
/// <para>
/// 使用範圍的紀律：這個鎖只保護短暫的臨界區（例如「建連結→寫設定→啟動遊戲」）。
/// 長時間的狀態（測試場次進行中）改由狀態檔維持，否則忘記停止的場次會把鎖
/// 留在長壽的 MCP server 程序名下，殘骸回收永遠不會觸發。
/// </para>
/// </summary>
public sealed class CriticalSectionLock(StoreDirectories store, IProcessHost processes)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>有界等待版本的輪詢間隔。鎖的持有者是「另一個工作的短臨界區」，100ms 足夠即時。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>取得鎖，回傳可用於釋放的 token。取不到時拋出 <see cref="LockHeldException"/>。</summary>
    public string Acquire(string name, IReadOnlyDictionary<string, string>? details = null)
    {
        var path = LockPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SweepStaleGraveyards(Path.GetDirectoryName(path)!);

        // 只允許接管殘骸鎖一次：第一輪發現持有者已死就刪掉重試，
        // 第二輪若仍失敗就是真的有人持有（或有另一個程序同時接管成功）。
        foreach (var mayReclaim in (ReadOnlySpan<bool>)[true, false])
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);

                var record = new LockRecord
                {
                    Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(18)),
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
                if (TryReclaimStaleLock(path, mayReclaim, out var holderProcessId))
                {
                    continue;
                }

                throw new LockHeldException(name, holderProcessId);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && mayReclaim)
            {
                // 建檔失敗但鎖檔已不在（上面的 File.Exists 為 false）：持有者剛好釋放。
                // Windows 上刪除中的檔案在最後一個 handle 關閉前，CreateNew 拿到的是 UnauthorizedAccessException。
                // 不能讓它漏出去——有界等待只重試 LockHeldException。稍等一下重試建檔，只重試一次：
                // 第二輪仍失敗就是真正的 I/O 或權限錯誤，原樣拋出而不是誤報成鎖被持有。
                Thread.Sleep(20);
                continue;
            }
        }

        throw new LockHeldException(name, null);
    }

    /// <summary>
    /// 取得鎖，取不到時在 <paramref name="timeout"/> 內輪詢等待；逾時仍拋出
    /// <see cref="LockHeldException"/>。
    ///
    /// <para>
    /// 用途是「短暫的重疊」：例如 Mod 索引要在 rebuild_index 的寫交易期間提交
    /// 一個毫秒級的交易，等幾秒就過去了。呼叫端必須把逾時當成可重試的失敗，
    /// 而不是永久失敗。
    /// </para>
    /// </summary>
    public string Acquire(string name, TimeSpan timeout, IReadOnlyDictionary<string, string>? details = null)
    {
        // 逾時量測用單調時鐘：DateTime.UtcNow 受 NTP 跳變影響，會拉長或縮短等待。
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return Acquire(name, details);
            }
            catch (LockHeldException) when (elapsed.Elapsed < timeout)
            {
                Thread.Sleep(PollInterval);
            }
        }
    }

    private bool TryReclaimStaleLock(string path, bool mayReclaim, out int? holderProcessId)
    {
        var (outcome, existing) = ReadWithRetry(path);
        holderProcessId = existing?.ProcessId;

        var stale = outcome switch
        {
            // 內容壞掉的鎖檔視為殘骸——留著它只會讓工具永遠卡住。
            // 但剛建立的不算：Unix 上 FileShare.None 是「建檔後才上 flock」，
            // 中間一瞬間讀到的空檔是正在寫入的活鎖。
            ReadOutcome.Corrupt => IsPastCorruptGrace(path),
            ReadOutcome.Ok => IsStale(existing),
            // 檔案在讀取前消失：持有者剛釋放，直接重試建檔。
            ReadOutcome.Missing => true,
            _ => false,
        };

        // ReclaimStale 回傳 false 代表「搬走的不是當初判定的殘骸」，
        // 已嘗試歸還，本輪視為被持有，不重試建檔。
        return mayReclaim && stale && ReclaimStale(path, existing, expectFile: outcome != ReadOutcome.Missing);
    }

    private static (ReadOutcome outcome, LockRecord? record) ReadWithRetry(string path)
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

        return (outcome, existing);
    }

    /// <summary>釋放鎖。token 不符時什麼都不做——避免誤刪別人接管後的鎖。</summary>
    public void Release(string name, string token)
    {
        var path = LockPath(name);
        var existing = Read(path);

        if (existing is not null && string.Equals(existing.Token, token, StringComparison.Ordinal))
        {
            DeleteOwnLock(path);
        }
    }

    /// <summary>
    /// 刪除自己持有的鎖檔。等待者正好在讀鎖檔時，Windows 上的刪除會撞上分享違規；
    /// 直接吞掉的話鎖就永遠不會釋放——持有者還活著，不會被當成殘骸回收，之後每個等待者都只能等到逾時。
    /// 讀取只佔用幾毫秒，短暫重試即可。只在刪除本身失敗時重試：刪除成功後檔案若又出現，
    /// 那是別人剛建立的新鎖，不能碰。
    /// </summary>
    private static void DeleteOwnLock(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(20);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 重試仍失敗：盡力而為，不阻礙主流程（與原本的行為相同）。
                return;
            }
        }
    }

    /// <summary>以 using 持有鎖，離開範圍時自動釋放。</summary>
    public IDisposable Hold(string name, IReadOnlyDictionary<string, string>? details = null)
    {
        var token = Acquire(name, details);
        return new Holder(this, name, token);
    }

    /// <summary>以 using 持有鎖，取不到時最多等 <paramref name="timeout"/>。</summary>
    public IDisposable Hold(string name, TimeSpan timeout, IReadOnlyDictionary<string, string>? details = null)
    {
        var token = Acquire(name, timeout, details);
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

    /// <summary>內容壞掉的鎖檔要放多久才當殘骸。持有者寫入鎖紀錄只要幾毫秒，5 秒綽綽有餘。</summary>
    private static readonly TimeSpan CorruptGrace = TimeSpan.FromSeconds(5);

    private static bool IsPastCorruptGrace(string path)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > CorruptGrace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
            // 檔案可能已被其他行程刪除或鎖定，盡力清理即可，不阻礙主流程。
        }
    }

    /// <summary>
    /// 嘗試接管殘骸鎖：先把鎖檔**改名**到一個專屬名稱，再刪除。
    ///
    /// <para>
    /// 直接用 <see cref="File.Delete"/> 有 TOCTOU：在「讀到鎖檔並判定為殘骸」
    /// 與「刪除」之間，另一個程序可能已經完成接管並建立自己的鎖，
    /// 那一下刪掉的是一個**活著**的鎖——於是兩個程序同時認為自己持有臨界區，
    /// 正是這個類別極力避免的情境。
    /// </para>
    /// <para>
    /// 改名把視窗縮得很小，但**沒有完全關閉 ABA 競態**：A、B 同時判定 X 為殘骸，
    /// B 先改名、刪除、重建新鎖，A 再改名仍會成功並搬走 B 的活鎖。
    /// 因此改名成功後會比對搬走檔案的 token 是否與分類當時讀到的殘骸一致；
    /// 不一致代表中間檔案被換過，嘗試把檔案歸還原位並放棄接管（回傳 false），
    /// 呼叫端視為鎖被持有而不重試建檔。無條件刪除只會發生在 token 一致、
    /// 或當初根本讀不到內容（損毀）的情況。
    /// </para>
    /// </summary>
    /// <returns>true 代表可以重試建檔；false 代表搬走的是別人的新鎖，已嘗試歸還。</returns>
    private static bool ReclaimStale(string path, LockRecord? expected, bool expectFile)
    {
        var graveyard = $"{path}.stale-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";

        try
        {
            File.Move(path, graveyard);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 別人搶先接管了（來源檔已不存在），或無權限。兩種都交給下一輪重試。
            return true;
        }

        if (expected?.Token is string expectedToken)
        {
            var moved = Read(graveyard);

            if (!string.Equals(moved?.Token, expectedToken, StringComparison.Ordinal))
            {
                TryRestore(graveyard, path);
                return false;
            }
        }
        else if (!expectFile)
        {
            // 分類時檔案不存在，改名卻成功：中間有新鎖建立，歸還並放棄。
            TryRestore(graveyard, path);
            return false;
        }

        TryDelete(graveyard);
        return true;
    }

    /// <summary>把誤搬的檔案搬回原位。原位已有新檔案時不覆寫，避免毀掉第三者的鎖。</summary>
    private static void TryRestore(string graveyard, string path)
    {
        try
        {
            File.Move(graveyard, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 原位已有新檔案（第三者已建鎖）或無權限：留著 graveyard，
            // 由週期性的殘留清理處理，不在此刪除別人的活鎖副本。
        }
    }

    /// <summary>
    /// 清理殘留的 <c>*.stale-*</c> 接管暫存（程序在改名後、刪除前崩潰會留下它們）。
    /// 只清超過 10 分鐘的，避免刪掉正與我們並行接管的另一個程序的暫存。
    /// </summary>
    private static void SweepStaleGraveyards(string locksDirectory)
    {
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(locksDirectory, "*.stale-*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 盡力清理即可。
            }
        }
    }

    private sealed class Holder(CriticalSectionLock owner, string name, string token) : IDisposable
    {
        public void Dispose() => owner.Release(name, token);
    }
}
