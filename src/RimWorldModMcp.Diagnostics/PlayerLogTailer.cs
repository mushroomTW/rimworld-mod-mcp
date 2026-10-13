using System.Text;
using System.Text.RegularExpressions;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 持續讀取 RimWorld 的 Player.log，把錯誤與警告收進診斷。
///
/// <para>
/// Bridge 連不上時（埠被佔、Mod 沒載入成功）這是唯一的診斷來源，
/// 所以即使 Bridge 正常也一併讀——兩邊的資訊互補。
/// </para>
/// </summary>
public sealed partial class PlayerLogTailer(TestSessionStore sessions, DiagnosticStore diagnostics)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Lock _stateGate = new();
    private string? _currentRun;

    /// <summary>
    /// 檔案身分（F08）：建立時間＋上次見到的長度＋開頭位元組前綴。
    /// 只比長度會漏掉「重建後快速長大」（新檔已超過舊 offset）；
    /// 只比建立時間會被 Windows 的檔案通道效應（delete＋重建仍保留建立時間）騙過，
    /// 所以再比開頭前綴——追加寫入不會改變檔頭，重建／截斷重寫幾乎一定改變它。
    /// </summary>
    private readonly Dictionary<string, (DateTime? CreationUtc, long Length, string Prefix)> _identities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 上一批尾的診斷（F09 接續用）。帶時間戳：續行若與標題相隔超過
    /// <see cref="ContinuationIdleTimeout"/>，視為無關（前一筆已完整），不再合併，
    /// 避免久候的孤立堆疊行誤併到早已結束的診斷。
    /// </summary>
    private readonly Dictionary<string, (DiagnosticIdentity Identity, DateTimeOffset At)> _lastEntry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 上一批結束時仍在其中的結構化紀錄，值為它是否是診斷。輪詢邊界可能切在一筆紀錄中間，
    /// 下一批開頭直到下一個帶層級的行之前，都還屬於那一筆。
    /// </summary>
    private readonly Dictionary<string, bool> _openRecord = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ContinuationIdleTimeout = TimeSpan.FromMinutes(5);

    private const int IdentityPrefixBytes = 64;

    /// <summary>
    /// 單次輪詢最多讀取的位元組數。遊戲啟動時可能一次寫入數 MB，
    /// 沒有限制的話會一次配置一大塊；有上限也只是分成幾輪追上
    ///（<c>offset</c> 會推進，不會漏）。
    /// </summary>
    private const int MaxBytesPerPoll = 4 * 1024 * 1024;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 以路徑為鍵記住讀到哪裡。
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? currentRun = null;

        using var timer = new PeriodicTimer(PollInterval);

        while (await SafeWaitAsync(timer, cancellationToken))
        {
            var session = sessions.Read();

            // 換一輪測試就丟掉舊的位置，否則第二輪會從第一輪的結尾開始而漏讀。
            if (session.RunId != currentRun)
            {
                currentRun = session.RunId;
                offsets.Clear();
            }

            if (session.State != "running" || string.IsNullOrEmpty(session.PlayerLog) || !File.Exists(session.PlayerLog))
            {
                continue;
            }

            ReadNewLines(session, offsets);
        }
    }

    /// <summary>
    /// 讀取自上次之後新增的完整行並分類。以**位元組**為單位推進讀取位置。
    ///
    /// <para>
    /// 舊作法是 <c>StreamReader.ReadToEnd()</c> 讀成字串，再以
    /// <c>Encoding.UTF8.GetByteCount(已讀內容)</c> 回推推進量。那個回推在遇到
    /// 非法 UTF-8 時會失真：解碼器把非法序列換成 U+FFFD（3 位元組），而它可能
    /// 只消耗 1~2 個位元組，於是推進量**大於**實際讀取量，下一次 <c>Seek</c>
    /// 直接跳過真實內容——靜默漏讀日誌行。Player.log 由遊戲與各 Mod 共同寫入，
    /// 出現非法位元組並不罕見，而漏掉的正好是診斷。
    /// </para>
    /// </summary>
    internal void ReadNewLines(TestSession session, Dictionary<string, long> offsets)
    {
        var path = session.PlayerLog!;

        lock (_stateGate)
        {
            if (!string.Equals(_currentRun, session.RunId, StringComparison.Ordinal))
            {
                _currentRun = session.RunId;
                _identities.Clear();
                _lastEntry.Clear();
                _openRecord.Clear();
            }
        }

        FileInfo info;

        try
        {
            info = new FileInfo(path);
        }
        catch (IOException)
        {
            return;
        }

        long length;
        DateTime? creationUtc;

        try
        {
            length = info.Length;

            // Linux 上 .NET 的 CreationTimeUtc 不是真正的建立時間（取自 mtime／ctime），
            // 追加寫入就會改變它，拿來判斷身分會把每次追加誤認為重建。視為未知，
            // 改由長度、檔頭前綴與 Player-prev.log 戳記判斷。
            creationUtc = OperatingSystem.IsLinux() ? null : info.CreationTimeUtc;
        }
        catch (IOException)
        {
            return;
        }

        var offset = ResolveInitialOffset(path, session, creationUtc, offsets);

        string prefix;

        try
        {
            prefix = ReadPrefix(path, length);
        }
        catch (IOException)
        {
            return;
        }

        offset = AdjustOffsetForIdentity(path, length, creationUtc, prefix, offset);

        if (length <= offset)
        {
            return;
        }

        try
        {
            // FileShare 必須含 Delete，否則會擋住遊戲輪替 log 檔。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);

            var available = (int)Math.Min(length - offset, MaxBytesPerPoll);
            var buffer = new byte[available];

            var read = stream.ReadAtLeast(buffer, available, throwOnEndOfStream: false);

            if (read == 0)
            {
                return;
            }

            // 只消費「完整的行」。遊戲可能正寫到一半——沒有結尾換行的最後
            // 一段先留著，下一輪它補完後才讀，否則半行會被當成完整行分類，
            // 剩餘部分之後又變成另一行。
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', read - 1, read);

            if (lastNewline < 0)
            {
                // 整段都沒有換行：通常是檔案尾的半行，不推進位置，
                // 下一輪連同新內容一起讀。位元組位置本身就是狀態，不需要額外保存半行。
                // 例外：讀滿 MaxBytesPerPoll 仍無換行，代表單行超過上限。
                if (available >= MaxBytesPerPoll)
                {
                    offsets[path] = offset + available;
                }

                return;
            }

            var consumed = lastNewline + 1;
            var complete = Encoding.UTF8.GetString(buffer, 0, consumed);
            var lines = complete.Split('\n');

            var startIndex = HandleStackContinuation(lines, path);
            RecordBatchDiagnostics(lines, startIndex, path, session.RunId);

            // 推進量是**實際讀取的位元組數**，與解碼結果無關。
            offsets[path] = offset + consumed;

            lock (_stateGate)
            {
                _identities[path] = (creationUtc, length, prefix);
            }
        }
        catch (IOException)
        {
            // 遊戲正在寫入時偶爾會讀不到，下一輪再試。
        }
    }

    /// <summary>
    /// 同目錄 Player-prev.log 的「大小:修改時間」戳記；不存在時為 missing。
    /// 啟動遊戲前記一次，之後不同就代表遊戲已把舊 Player.log 輪替成它。
    /// 改名保留修改時間，所以輪替後的戳記是舊 Player.log 的，與先前的 prev 不同。
    /// </summary>
    public static string PreviousLogStamp(string playerLog)
    {
        var directory = Path.GetDirectoryName(playerLog);
        var info = new FileInfo(Path.Combine(directory ?? string.Empty, "Player-prev.log"));

        try
        {
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (IOException)
        {
            return "unknown";
        }
    }

    /// <summary>讀取檔頭前綴供身分比對。呼叫端已處理 IOException。</summary>
    private static string ReadPrefix(string path, long length)
    {
        var size = (int)Math.Min(length, IdentityPrefixBytes);

        if (size <= 0)
        {
            return string.Empty;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[size];
        var read = stream.ReadAtLeast(buffer, size, throwOnEndOfStream: false);
        return Convert.ToHexString(buffer.AsSpan(0, read));
    }

    private static long ResolveInitialOffset(string path, TestSession session, DateTime? creationUtc, Dictionary<string, long> offsets)
    {
        if (offsets.TryGetValue(path, out var offset))
        {
            return offset;
        }

        // 第一次讀這個檔案時，從啟動遊戲前記下的位置開始，
        // 才不會把上一輪遊戲留下的內容當成這次的診斷。
        offset = session.LogOffset;

        // F08：若檔案是在本場次開始後建立的（重建／輪替），LogOffset 指向的是舊檔，
        // 必須從頭讀，否則新檔開頭的錯誤會被跳過。
        if (offset > 0 && session.StartedAt is { } startedAt)
        {
            var sessionStart = DateTimeOffset.FromUnixTimeMilliseconds(startedAt).UtcDateTime;

            if (creationUtc is { } created && created >= sessionStart - TimeSpan.FromSeconds(1))
            {
                offset = 0;
            }
        }

        // Windows 的檔案通道效應會讓新檔沿用舊檔的建立時間，上面的判斷就失效了。
        // 遊戲輪替時舊檔改名成 Player-prev.log，它的戳記一變就代表目前這份是新檔。
        if (offset > 0
            && session.PreviousLogStamp is { } before
            && PreviousLogStamp(path) != before)
        {
            offset = 0;
        }

        return offset;
    }

    private long AdjustOffsetForIdentity(string path, long length, DateTime? creationUtc, string prefix, long offset)
    {
        lock (_stateGate)
        {
            // F08：追蹤檔案身分。僅以 length < offset 判斷時，重建後快速長大
            // （新檔已超過舊 offset）會被誤認為原檔而跳過開頭。
            if (_identities.TryGetValue(path, out var known)
                && (known.CreationUtc != creationUtc
                    || length < known.Length
                    || !prefix.StartsWith(known.Prefix, StringComparison.Ordinal)))
            {
                offset = 0;
                _lastEntry.Remove(path);
                _openRecord.Remove(path);
            }
        }

        // 檔案變短代表遊戲重啟後截斷了 log，位置要歸零重讀。
        if (length < offset)
        {
            offset = 0;

            lock (_stateGate)
            {
                _lastEntry.Remove(path);
                _openRecord.Remove(path);
            }
        }

        return offset;
    }

    private int HandleStackContinuation(string[] lines, string path)
    {
        DiagnosticIdentity? pending = null;
        bool? openRecordIsEntry;

        lock (_stateGate)
        {
            openRecordIsEntry = _openRecord.TryGetValue(path, out var isEntry) ? isEntry : null;

            if (_lastEntry.TryGetValue(path, out var last))
            {
                if (DateTimeOffset.UtcNow - last.At > ContinuationIdleTimeout)
                {
                    // 閒置過久：前一筆已完整，清除接續資格。
                    _lastEntry.Remove(path);
                }
                else
                {
                    pending = last.Identity;
                }
            }
        }

        // 上一批停在一筆結構化紀錄中間。下一個帶層級的行之前都屬於它——
        // 是診斷就併進去（不含引擎附的呼叫堆疊），不是（例如 INFO）就整段丟掉，
        // 不能併到更早的診斷。
        if (openRecordIsEntry is not { } openIsEntry)
        {
            return 0;
        }

        var leading = lines.Select(Normalise).TakeWhile(text => !StructuredLineRegex().IsMatch(text)).ToList();
        var body = leading.Where(text => text.Length > 0 && !EngineStackFrameRegex().IsMatch(text)).ToList();

        if (openIsEntry && pending is not null && body.Count > 0)
        {
            diagnostics.AppendContinuation(pending, string.Join('\n', body));
        }

        return leading.Count;
    }

    private void RecordBatchDiagnostics(string[] lines, int startIndex, string path, string? runId)
    {
        var batch = ClassifyBatch(lines.Skip(startIndex), out var trailingRecordIsEntry);

        // 記下批尾停在哪一筆結構化紀錄裡。這批沒有任何帶層級的行時，
        // 整批都已當作上一筆的續行處理，狀態不變。
        if (trailingRecordIsEntry is { } isEntry)
        {
            lock (_stateGate)
            {
                _openRecord[path] = isEntry;
            }
        }

        // 整批一次寫入：錯誤風暴時逐行各做一次全檔 read+serialize 是 O(n²) IO。
        if (batch.Count > 0)
        {
            diagnostics.AddRange(batch, "player.log", runId);

            lock (_stateGate)
            {
                var last = batch[^1];
                _lastEntry[path] = (new DiagnosticIdentity(last.Type, last.FirstLine, "player.log", runId), DateTimeOffset.UtcNow);
            }
        }
    }

    /// <summary>
    /// 把一批日誌行分類成診斷。
    ///
    /// <para>
    /// 每筆紀錄以「時間 層級 [分類]」開頭，之後到下一個開頭之前的行（多行訊息、堆疊）
    /// 都屬於同一筆：是診斷就併進去（引擎附的呼叫堆疊除外），不是就整段略過。
    /// 一個例外在 Player.log 裡通常有 5~20 行堆疊，逐行各成一筆會把保留容量（200 筆）
    /// 瞬間塞滿、把真正的錯誤擠掉，同時把 error_count 灌成假的高數字——
    /// 而 agent 會照著那些數字去追不存在的問題。
    /// </para>
    /// <para>
    /// 第一筆紀錄之前的行（檔頭等）沒有所屬紀錄，直接丟棄。
    /// </para>
    /// </summary>
    internal static List<(string Type, string FirstLine, string Text)> ClassifyBatch(IEnumerable<string> lines)
        => ClassifyBatch(lines, out _);

    /// <param name="lines">一批日誌行。</param>
    /// <param name="trailingRecordIsEntry">批尾停在的那筆紀錄是否是診斷；這批沒有任何帶層級的行時為 null。</param>
    internal static List<(string Type, string FirstLine, string Text)> ClassifyBatch(IEnumerable<string> lines, out bool? trailingRecordIsEntry)
    {
        var batch = new List<(string Type, string FirstLine, string Text)>();
        bool? currentRecordIsEntry = null;

        foreach (var line in lines)
        {
            var text = Normalise(line);

            if (text.Length == 0)
            {
                continue;
            }

            var structured = StructuredLineRegex().Match(text);

            if (!structured.Success)
            {
                if (currentRecordIsEntry == true && !EngineStackFrameRegex().IsMatch(text))
                {
                    // 訊息以換行開頭時標題行沒有內容，第一個續行才是訊息的第一行。
                    var previous = batch[^1];
                    batch[^1] = previous.Text.Length == 0
                        ? (previous.Type, text, text)
                        : (previous.Type, previous.FirstLine, previous.Text + "\n" + text);
                }

                continue;
            }

            var item = ClassifyStructured(structured.Groups["level"].Value, structured.Groups["message"].Value);

            if (item is { } entry)
            {
                batch.Add(entry);
            }

            currentRecordIsEntry = item is not null;
        }

        // 標題行沒有內容、這批也沒有續行的紀錄沒有可回報的文字，丟掉。批尾那筆被丟掉時，
        // 下一批的續行不能併到更早的診斷，所以批尾視為非診斷。
        if (currentRecordIsEntry == true && batch[^1].Text.Length == 0)
        {
            currentRecordIsEntry = false;
        }

        batch.RemoveAll(entry => entry.Text.Length == 0);
        trailingRecordIsEntry = currentRecordIsEntry;

        return batch;
    }

    /// <summary>
    /// 日誌行：<c>2026-10-13 01:29:00.123 ERROR [Verse] 訊息</c>。層級固定五字元寬、
    /// 右靠（" WARN"），Normalise 之後前導空白已併進分隔。分類名稱一律剝掉：
    /// Bridge 回報的是原始訊息，診斷要以相同文字去重。
    /// </summary>
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}\D\d{2}\D\d{2}\.\d{3} +(?<level>TRACE|DEBUG|INFO|WARN|ERROR|CRIT)(?: \[[^\]\s]*\])?(?: (?<message>.*))?$")]
    private static partial Regex StructuredLineRegex();

    /// <summary>
    /// 引擎替每一筆紀錄附上的呼叫端堆疊（FileLog.FormatStack：<c>at 型別.方法(...) [0xIL] in 檔案:行</c>）。
    /// 這些不放進診斷：DiagnosticStore 的去重簽章只看 at 行，留著的話同一呼叫點的不同錯誤
    /// 會被併成一筆，也對不上不帶堆疊的 Bridge 回報。訊息本身含的例外堆疊（.NET 的格式沒有 [0x..]）照留。
    /// </summary>
    [GeneratedRegex(@"^at .+ \[0x[0-9A-Fa-f]+\](?: in .+)?$")]
    private static partial Regex EngineStackFrameRegex();

    /// <summary>
    /// 有明確層級的日誌行：照層級分類，不再靠關鍵字。訊息以換行開頭時 <paramref name="message"/> 是空的，
    /// 錯誤與警告仍回一筆空文字的診斷，由 ClassifyBatch 以第一個續行補上。
    /// </summary>
    private static (string Type, string FirstLine, string Text)? ClassifyStructured(string level, string message)
    {
        if (message.Length > 0 && PerformanceMarkers.Matches(message))
        {
            return ("performance", message, message);
        }

        return level switch
        {
            "ERROR" or "CRIT" => ("error", message, message),
            "WARN" => ("warning", message, message),
            _ => null,
        };
    }

    /// <summary>
    /// 去掉縮排與 BOM。關掉 BOM 自動偵測後，檔案開頭的 BOM 會以字元形式
    /// 出現在第一行。
    /// </summary>
    private static string Normalise(string line) => line.Trim().TrimStart('﻿').Trim();

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
