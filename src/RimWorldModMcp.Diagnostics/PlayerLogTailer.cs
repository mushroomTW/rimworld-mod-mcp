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

        if (!offsets.TryGetValue(path, out var offset))
        {
            // 第一次讀這個檔案時，從啟動遊戲前記下的位置開始，
            // 才不會把上一輪遊戲留下的內容當成這次的診斷。
            offset = session.LogOffset;
        }

        long length;

        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return;
        }

        // 檔案變短代表遊戲重啟後截斷了 log，位置要歸零重讀。
        if (length < offset)
        {
            offset = 0;
        }

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
                // 下一輪連同新內容一起讀。位元組位置本身就是狀態，
                // 不需要額外保存半行。
                //
                // 例外：讀滿 MaxBytesPerPoll 仍無換行，代表單行超過上限。
                // 若不推進，每輪都會重讀同樣 4MB 且位置永不前進。
                // Player.log 實務上不會有這種行，這裡直接跳過該段以保證推進。
                if (available >= MaxBytesPerPoll)
                {
                    offsets[path] = offset + available;
                }

                return;
            }

            var consumed = lastNewline + 1;
            var complete = Encoding.UTF8.GetString(buffer, 0, consumed);

            // 整批一次寫入：錯誤風暴時逐行各做一次全檔 read+serialize 是 O(n²) IO。
            diagnostics.AddRange(ClassifyBatch(complete.Split('\n')), "player.log", session.RunId);

            // 推進量是**實際讀取的位元組數**，與解碼結果無關。
            offsets[path] = offset + consumed;
        }
        catch (IOException)
        {
            // 遊戲正在寫入時偶爾會讀不到，下一輪再試。
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

    private static (string Type, string FirstLine, string Text)? Classify(string line)
    {
        var text = Normalise(line);

        if (text.Length == 0)
        {
            return null;
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
