using System.Text;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 持續讀取 RimWorld 的 Player.log，把錯誤與警告收進診斷。
///
/// <para>
/// Bridge 連不上時（埠被佔、Mod 沒載入成功）這是唯一的診斷來源，
/// 所以即使 Bridge 正常也一併讀——兩邊的資訊互補。
/// </para>
/// </summary>
public sealed class PlayerLogTailer(TestSessionStore sessions, DiagnosticStore diagnostics)
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
    /// 啟動遊戲前記一次，之後不同就代表 Unity 已把舊 Player.log 輪替成它。
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
        // Unity 輪替時舊檔改名成 Player-prev.log，它的戳記一變就代表目前這份是新檔。
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
            }
        }

        // 檔案變短代表遊戲重啟後截斷了 log，位置要歸零重讀。
        if (length < offset)
        {
            offset = 0;

            lock (_stateGate)
            {
                _lastEntry.Remove(path);
            }
        }

        return offset;
    }

    private int HandleStackContinuation(string[] lines, string path)
    {
        var startIndex = 0;
        DiagnosticIdentity? pending = null;

        lock (_stateGate)
        {
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

        if (pending is not null)
        {
            var leading = new List<string>();

            foreach (var raw in lines)
            {
                var text = Normalise(raw);

                if (text.Length == 0 || !IsStackContinuation(text))
                {
                    break;
                }

                leading.Add(text);
            }

            if (leading.Count > 0)
            {
                diagnostics.AppendContinuation(pending, string.Join('\n', leading));
                startIndex = leading.Count;
            }
            else
            {
                // 新一批不是續行：上一筆診斷已完整，清除接續資格。
                lock (_stateGate)
                {
                    _lastEntry.Remove(path);
                }
            }
        }

        return startIndex;
    }

    private void RecordBatchDiagnostics(string[] lines, int startIndex, string path, string? runId)
    {
        var batch = ClassifyBatch(lines.Skip(startIndex));

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
    /// 堆疊追蹤的續行會**併進前一筆**而不是各自成一筆。一個例外在 Player.log 裡
    /// 通常有 5~20 行堆疊，逐行各成一筆會把保留容量（200 筆）瞬間塞滿、
    /// 把真正的錯誤擠掉，同時把 error_count 灌成假的高數字——而 agent 會照著
    /// 那些數字去追不存在的問題。
    /// </para>
    /// <para>
    /// 續行只在「緊接的前一行本身就是一筆診斷」時才併入；否則（例如一段孤立的
    /// 堆疊行）直接丟棄，避免誤併到更早的無關紀錄。
    /// </para>
    /// </summary>
    internal static List<(string Type, string FirstLine, string Text)> ClassifyBatch(IEnumerable<string> lines)
    {
        var batch = new List<(string Type, string FirstLine, string Text)>();
        var previousLineWasEntry = false;

        foreach (var line in lines)
        {
            var text = Normalise(line);

            if (text.Length == 0)
            {
                previousLineWasEntry = false;
                continue;
            }

            if (previousLineWasEntry && IsStackContinuation(text))
            {
                var previous = batch[^1];
                batch[^1] = (previous.Type, previous.FirstLine, previous.Text + "\n" + text);
                continue;
            }

            if (Classify(text) is { } item)
            {
                batch.Add(item);
                previousLineWasEntry = true;
                continue;
            }

            previousLineWasEntry = false;
        }

        return batch;
    }

    /// <summary>
    /// 是否是堆疊追蹤的續行。縮排已經被 <see cref="Normalise"/> 去掉，
    /// 所以判斷 "at " 開頭即可（.NET 的堆疊行形如 <c>at Verse.Thing.Tick()</c>）。
    /// </summary>
    private static bool IsStackContinuation(string trimmedLine)
        => trimmedLine.StartsWith("at ", StringComparison.Ordinal);

    /// <summary>
    /// 去掉縮排與 BOM。關掉 BOM 自動偵測後，檔案開頭的 BOM 會以字元形式
    /// 出現在第一行。
    /// </summary>
    private static string Normalise(string line) => line.Trim().TrimStart('﻿').Trim();

    /// <summary>
    /// 含 error 字樣、實際卻是 Log.Warning 的訊息。「Translation data ... has N errors」
    /// 來自 LoadedLanguage，而使用者的語言設定會隨 Prefs.xml 帶進每一場測試——
    /// 不排除的話每場都憑空多一個 error。
    /// </summary>
    private static readonly string[] KnownWarnings =
    [
        "Translation data for language",
        "(using undefined sound instead)",
    ];

    /// <summary>不含 error／exception 字樣的 Log.Error（DirectXmlCrossRefLoader、DefDatabase）。</summary>
    private static readonly string[] KnownErrors =
    [
        "Could not resolve cross-reference",
        "Failed to find ",
    ];

    private static (string Type, string FirstLine, string Text)? Classify(string line)
    {
        var text = Normalise(line);

        if (text.Length == 0)
        {
            return null;
        }

        // 效能標記是 Mod 作者明確的選擇，優先於關鍵字：效能行常含 error 以外的字樣，
        // 但萬一含了也不該被當成錯誤。
        if (PerformanceMarkers.Matches(text))
        {
            return ("performance", text, text);
        }

        // Player.log 沒有層級標記，只能靠關鍵字；已知會被關鍵字誤判的 RimWorld 訊息先按真實層級處理。
        if (KnownWarnings.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return ("warning", text, text);
        }

        if (KnownErrors.Any(marker => text.StartsWith(marker, StringComparison.Ordinal)))
        {
            return ("error", text, text);
        }

        var isError = text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase);

        var isWarning = text.Contains("warning", StringComparison.OrdinalIgnoreCase);

        if (!isError && !isWarning)
        {
            return null;
        }

        return (isError ? "error" : "warning", text, text);
    }

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
