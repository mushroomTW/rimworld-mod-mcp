using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimWorldModMcp.Core;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>一筆測試診斷。</summary>
public sealed record DiagnosticRecord
{
    [JsonPropertyName("hash")]
    public required string Hash { get; init; }

    /// <summary>error、warning、diagnostic、loaded_mods 或 performance。</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("first_line")]
    public required string FirstLine { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary>bridge 或 player.log。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("at")]
    public required long At { get; init; }

    /// <summary>
    /// 寫入序號，與 <see cref="At"/> 組成分頁游標的複合鍵。
    /// <see cref="At"/> 只到毫秒，同一毫秒可能有多筆（AddRange 一次寫入整批），
    /// 純時間游標會在切頁時把同毫秒整批帶上、或讓下半批永遠漏掉。
    /// </summary>
    [JsonPropertyName("sequence")]
    public required long Sequence { get; init; }

    // 注意：這個型別刻意沒有 token 屬性。
    // Bridge 送來的每一行都夾帶驗證用的 token，Python 版把整個訊息原封不動存進
    // diagnostics.json 並經 MCP 回傳給客戶端——token 因此外洩到磁碟與回應中。
    // 在型別層面不存在這個欄位，就不可能再犯同樣的錯。
}

/// <summary>
/// 分頁游標的複合鍵 (At, Sequence)。
/// At 只到毫秒，同一毫秒可能有多筆，必須配上 Sequence 才有穩定全序。
/// 用一個型別把兩個欄位綁在一起，避免它們在 Store／Tools／測試之間散落傳遞。
/// </summary>
public sealed record DiagnosticCursor(long At, long Sequence)
{
    /// <summary>此紀錄是否在此游標之後（新出現或重複出現而更新）。</summary>
    public bool IsAfter(DiagnosticRecord record)
        => record.At > At || (record.At == At && record.Sequence > Sequence);
}

/// <summary>定位一筆診斷的鍵：型別＋首行＋來源＋場次。</summary>
public sealed record DiagnosticIdentity(string Type, string FirstLine, string Source, string? RunId);

/// <summary>
/// 整個測試場次的累計筆數。
///
/// <para>
/// 這與「保留中的紀錄數」是兩件事：<see cref="DiagnosticStore"/> 有容量上限，
/// crash loop 會把紀錄擠掉。累計數只能增加，呼叫端才能據此判斷「這一場有沒有錯」。
/// </para>
/// </summary>
public sealed record DiagnosticTotals
{
    [JsonPropertyName("error")]
    public int Error { get; init; }

    [JsonPropertyName("warning")]
    public int Warning { get; init; }
}

/// <summary>
/// 測試診斷的儲存與去重。
///
/// <para>
/// 相同的錯誤在一次測試中可能出現上百次（例如每 tick 都拋的例外），
/// 所以用簽章雜湊去重並累加 count，只保留最近的一批。
/// </para>
/// </summary>
public sealed class DiagnosticStore(StoreDirectories store)
{
    /// <summary>保留的診斷筆數上限。</summary>
    private const int Capacity = 200;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>單筆診斷內容的字元上限。Bridge 端不截斷，磁碟成長必須在這裡封頂。</summary>
    private const int MaxTextChars = 64 * 1024;

    private readonly Lock _gate = new();

    /// <summary>
    /// 下一筆的寫入序號。實例層級遞增，重啟後從磁碟已有的最大序號接續
    ///（見 <see cref="EnsureSequenceInitialized"/>），避免新筆與舊筆的序號碰撞。
    /// 跨行程的併發寫入仍靠檔案鎖序列化，同一毫秒內由序號保證全序。
    /// </summary>
    private long _nextSequence;

    /// <summary>加入一筆診斷。相同簽章的既有紀錄會被合併並累加次數。</summary>
    public DiagnosticRecord Add(string type, string firstLine, string text, string source, string? runId)
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();

            var records = Read().ToList();
            EnsureSequenceInitialized(records);
            var (record, isNew) = MergeInto(records, type, firstLine, text, source, runId);

            Evict(records);
            AtomicJson.Write(store.DiagnosticsFile, records, JsonOptions);

            var errors = 0;
            var warnings = 0;
            CountByType(isNew ? type : string.Empty, ref errors, ref warnings);
            BumpTotals(errors, warnings);

            return record;
        }
    }

    /// <summary>
    /// 批次加入診斷，整批只做一次讀檔與一次寫檔。
    /// Player.log 的錯誤風暴每行一筆各自 read+serialize 是 O(n²) 的 IO。
    /// </summary>
    public void AddRange(
        IReadOnlyList<(string Type, string FirstLine, string Text)> items,
        string source,
        string? runId)
    {
        if (items.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();

            var records = Read().ToList();
            EnsureSequenceInitialized(records);

            var newErrors = 0;
            var newWarnings = 0;

            foreach (var (type, firstLine, text) in items)
            {
                var (_, isNew) = MergeInto(records, type, firstLine, text, source, runId);

                if (isNew)
                {
                    CountByType(type, ref newErrors, ref newWarnings);
                }
            }

            Evict(records);
            AtomicJson.Write(store.DiagnosticsFile, records, JsonOptions);
            BumpTotals(newErrors, newWarnings);
        }
    }

    /// <summary>
    /// 把跨輪詢才寫入的堆疊續行併入最近一筆診斷（F09）。
    /// 第一輪已把錯誤標題寫入、stack 在下一輪才出現時，下一批的 `at ...` 行
    /// 本身不含 error/warning，若無此合併會被丟棄。只有當 Type + FirstLine + Source + RunId
    /// 吻合的最新一筆**同時是整份清單的最後一筆**才合併——期間若已寫入其他診斷，
    /// 續行歸屬不明，寧可丟棄也不誤併（找不到或非末筆時什麼都不做）。
    /// </summary>
    public void AppendContinuation(DiagnosticIdentity identity, string continuationText)
    {
        if (string.IsNullOrWhiteSpace(continuationText))
        {
            return;
        }

        lock (_gate)
        {
            using var fileLock = AcquireFileLock();

            var records = Read().ToList();
            EnsureSequenceInitialized(records);

            var index = -1;

            for (var i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];

                if (string.Equals(r.Type, identity.Type, StringComparison.Ordinal)
                    && string.Equals(r.FirstLine, identity.FirstLine, StringComparison.Ordinal)
                    && string.Equals(r.Source, identity.Source, StringComparison.Ordinal)
                    && string.Equals(r.RunId, identity.RunId, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0 || index != records.Count - 1)
            {
                return;
            }

            var record = records[index];
            var merged = record.Text + "\n" + continuationText;

            if (merged.Length > MaxTextChars)
            {
                merged = merged[..MaxTextChars];
            }

            records[index] = record with
            {
                Text = merged,
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sequence = ++_nextSequence,
            };

            AtomicJson.Write(store.DiagnosticsFile, records, JsonOptions);
        }
    }

    /// <summary>
    /// 整個場次的累計筆數，**不受容量上限與 since_at 影響**。
    ///
    /// <para>
    /// 呼叫端必須用它而不是「數保留中的紀錄」：後者會隨著淘汰而下降，
    /// 讓輪詢中的 agent 把「已經被擠掉的錯誤」誤讀成「沒有錯誤」。
    /// </para>
    /// </summary>
    public DiagnosticTotals Totals => ReadTotals();

    /// <summary>累計新紀錄。已在 <see cref="_gate"/> 與檔案鎖內呼叫。</summary>
    private void BumpTotals(int errors, int warnings)
    {
        if (errors == 0 && warnings == 0)
        {
            return;
        }

        var totals = ReadTotals();
        AtomicJson.Write(
            store.DiagnosticTotalsFile,
            totals with { Error = totals.Error + errors, Warning = totals.Warning + warnings },
            JsonOptions);
    }

    private static void CountByType(string type, ref int errors, ref int warnings)
    {
        if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
        {
            errors++;
        }
        else if (string.Equals(type, "warning", StringComparison.OrdinalIgnoreCase))
        {
            warnings++;
        }
    }

    private DiagnosticTotals ReadTotals()
    {
        try
        {
            return JsonSerializer.Deserialize<DiagnosticTotals>(File.ReadAllText(store.DiagnosticTotalsFile))
                ?? new DiagnosticTotals();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DiagnosticTotals();
        }
    }

    /// <summary>
    /// 行程重啟後 _nextSequence 歸零，磁碟上舊紀錄的序號還在；
    /// 直接遞增會與舊序號碰撞、複合鍵全序倒退。每次寫入前先對齊到磁碟最大值。
    /// 已在 _gate 與檔案鎖內呼叫。
    /// </summary>
    private void EnsureSequenceInitialized(List<DiagnosticRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var maxOnDisk = records.Max(r => r.Sequence);

        if (maxOnDisk > _nextSequence)
        {
            _nextSequence = maxOnDisk;
        }
    }

    private (DiagnosticRecord Record, bool IsNew) MergeInto(
        List<DiagnosticRecord> records,
        string type,
        string firstLine,
        string text,
        string source,
        string? runId)
    {
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
        }

        var hash = Signature(type, text);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sequence = ++_nextSequence;

        var existingIndex = records.FindIndex(r => r.Hash == hash);

        DiagnosticRecord record;

        if (existingIndex >= 0)
        {
            record = records[existingIndex] with { Count = records[existingIndex].Count + 1, At = now, Sequence = sequence };
            records[existingIndex] = record;

            return (record, false);
        }

        record = new DiagnosticRecord
        {
            Hash = hash,
            Type = type,
            FirstLine = firstLine,
            Text = text,
            Source = source,
            RunId = runId,
            Count = 1,
            At = now,
            Sequence = sequence,
        };

        records.Add(record);
        return (record, true);
    }

    /// <summary>
    /// 超出容量時淘汰「最久沒有更新」的紀錄。不能按 list 位置淘汰——
    /// 既有紀錄更新時留在原位，按位置砍會把出現最頻繁的錯誤最先丟掉。
    /// 以複合鍵 (At, Sequence) 比大小：同毫秒的多筆按序號分先後。
    /// </summary>
    private static void Evict(List<DiagnosticRecord> records)
    {
        while (records.Count > Capacity)
        {
            var oldestIndex = 0;

            for (var i = 1; i < records.Count; i++)
            {
                if ((records[i].At, records[i].Sequence).CompareTo((records[oldestIndex].At, records[oldestIndex].Sequence)) < 0)
                {
                    oldestIndex = i;
                }
            }

            records.RemoveAt(oldestIndex);
        }
    }

    /// <summary>
    /// 跨行程互斥。daemon 行程與 MCP server 行程都會對 diagnostics.json 做
    /// read-modify-write，行程內的 <see cref="_gate"/> 保護不到對方——
    /// 沒有這一層，server 的 Clear() 會被 daemon 的舊讀取寫回蓋掉，
    /// 上一場次的診斷污染新場次。
    ///
    /// <para>
    /// 鎖只被持有「讀檔 + 序列化 + 寫檔」這幾毫秒，所以等待上限放得很寬。
    /// 真的搶不到時仍然繼續執行（<see cref="AtomicJson"/> 保證單次寫入不會留下半份檔）——
    /// 這個取捨是刻意的：丟掉一筆診斷比偶發的 lost update 嚴重，因為診斷是
    /// 「Bridge 連不上時唯一的線索」。
    /// </para>
    /// </summary>
    private FileStream? AcquireFileLock()
    {
        var path = store.DiagnosticsFile + ".lock";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // 500 × 20ms = 10 秒。遠大於鎖的實際持有時間，把「搶不到」壓到幾乎不可能。
        for (var attempt = 0; attempt < 500; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    public IReadOnlyList<DiagnosticRecord> Read()
    {
        try
        {
            return JsonSerializer.Deserialize<List<DiagnosticRecord>>(File.ReadAllText(store.DiagnosticsFile)) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 只回游標之後有變動的紀錄：新出現的，以及重複出現而 count 增加的——
    /// 合併時 At 會更新，所以兩者都涵蓋。
    /// 游標是複合鍵 (At, Sequence)：sinceAt 之後的，或與 sinceAt
    /// 同毫秒但序號更大的（同毫秒整批寫入時，純時間游標會漏掉下半批）。
    /// 這是 agent 輪詢迴圈的游標：沒有它，每次都把整份清單重新塞進 context。
    /// </summary>
    public IReadOnlyList<DiagnosticRecord> ReadSince(long sinceAt, long lastSequence = 0)
        => ReadSince(new DiagnosticCursor(sinceAt, lastSequence));

    /// <summary>以 <see cref="DiagnosticCursor"/> 複合鍵查詢的 overload。</summary>
    public IReadOnlyList<DiagnosticRecord> ReadSince(DiagnosticCursor cursor)
        => [.. Read().Where(cursor.IsAfter)];

    public DiagnosticRecord? Find(string hash)
        => Read().FirstOrDefault(r => string.Equals(r.Hash, hash, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            AtomicJson.Write(store.DiagnosticsFile, (DiagnosticRecord[])[], JsonOptions);

            // 累計數屬於「這一場」，換場次就必須歸零，否則新場次會繼承上一場的數字。
            AtomicJson.Write(store.DiagnosticTotalsFile, new DiagnosticTotals(), JsonOptions);
        }
    }

    /// <summary>
    /// 算出一筆診斷的去重簽章。
    ///
    /// <para>
    /// 優先使用堆疊行（含 " at " 的行）——同一個例外每次拋出時的
    /// 前後文可能不同，但堆疊是一樣的。沒有堆疊就退回全文。
    /// </para>
    /// </summary>
    private static string Signature(string type, string text)
    {
        var stackLines = text
            .Split('\n')
            .Where(line => line.Contains(" at ", StringComparison.Ordinal))
            .ToList();

        var signature = stackLines.Count > 0 ? string.Join('\n', stackLines) : text;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{type}:{signature}"));

        return Convert.ToHexStringLower(digest)[..16];
    }
}
