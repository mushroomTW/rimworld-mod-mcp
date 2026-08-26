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

    /// <summary><c>error</c>、<c>warning</c>、<c>diagnostic</c>、<c>loaded_mods</c> 或 <c>performance</c>。</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("first_line")]
    public required string FirstLine { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    /// <summary><c>bridge</c> 或 <c>player.log</c>。</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("run_id")]
    public string? RunId { get; init; }

    [JsonPropertyName("count")]
    public required int Count { get; init; }

    [JsonPropertyName("at")]
    public required long At { get; init; }

    // 注意：這個型別刻意沒有 token 屬性。
    // Bridge 送來的每一行都夾帶驗證用的 token，Python 版把整個訊息原封不動存進
    // diagnostics.json 並經 MCP 回傳給客戶端——token 因此外洩到磁碟與回應中。
    // 在型別層面不存在這個欄位，就不可能再犯同樣的錯。
}

/// <summary>
/// 測試診斷的儲存與去重。
///
/// <para>
/// 相同的錯誤在一次測試中可能出現上百次（例如每 tick 都拋的例外），
/// 所以用簽章雜湊去重並累加 <c>count</c>，只保留最近的一批。
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

    /// <summary>加入一筆診斷。相同簽章的既有紀錄會被合併並累加次數。</summary>
    public DiagnosticRecord Add(string type, string firstLine, string text, string source, string? runId)
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();

            var records = Read().ToList();
            var record = MergeInto(records, type, firstLine, text, source, runId);

            Evict(records);
            AtomicJson.Write(store.DiagnosticsFile, records, JsonOptions);
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

            foreach (var (type, firstLine, text) in items)
            {
                MergeInto(records, type, firstLine, text, source, runId);
            }

            Evict(records);
            AtomicJson.Write(store.DiagnosticsFile, records, JsonOptions);
        }
    }

    private static DiagnosticRecord MergeInto(
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

        var existingIndex = records.FindIndex(r => r.Hash == hash);

        DiagnosticRecord record;

        if (existingIndex >= 0)
        {
            record = records[existingIndex] with { Count = records[existingIndex].Count + 1, At = now };
            records[existingIndex] = record;
        }
        else
        {
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
            };

            records.Add(record);
        }

        return record;
    }

    /// <summary>
    /// 超出容量時淘汰「最久沒有更新」的紀錄。不能按 list 位置淘汰——
    /// 既有紀錄更新時留在原位，按位置砍會把出現最頻繁的錯誤最先丟掉。
    /// </summary>
    private static void Evict(List<DiagnosticRecord> records)
    {
        while (records.Count > Capacity)
        {
            var oldestIndex = 0;

            for (var i = 1; i < records.Count; i++)
            {
                if (records[i].At < records[oldestIndex].At)
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
    /// 上一場次的診斷污染新場次。搶不到鎖時 best-effort 繼續
    /// （AtomicJson 仍保證單次寫入的原子性）。
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

        for (var attempt = 0; attempt < 100; attempt++)
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

    public DiagnosticRecord? Find(string hash)
        => Read().FirstOrDefault(r => string.Equals(r.Hash, hash, StringComparison.OrdinalIgnoreCase));

    public void Clear()
    {
        lock (_gate)
        {
            using var fileLock = AcquireFileLock();
            AtomicJson.Write(store.DiagnosticsFile, Array.Empty<DiagnosticRecord>(), JsonOptions);
        }
    }

    /// <summary>
    /// 算出一筆診斷的去重簽章。
    ///
    /// <para>
    /// 優先使用堆疊行（含 <c>" at "</c> 的行）——同一個例外每次拋出時的
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
