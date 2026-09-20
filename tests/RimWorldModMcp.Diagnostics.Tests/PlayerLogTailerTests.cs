using System.Text;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>
/// Player.log 的增量讀取。
///
/// <para>
/// 這是 Bridge 連不上時**唯一**的診斷來源，所以「漏讀」比「多讀」嚴重得多：
/// 多讀只是噪音，漏讀會讓 agent 判定「測試無錯誤」。
/// </para>
/// </summary>
public sealed class PlayerLogTailerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-tailer-" + Guid.NewGuid().ToString("n")[..12]);
    private readonly StoreDirectories _store;
    private readonly DiagnosticStore _diagnostics;
    private readonly PlayerLogTailer _tailer;

    public PlayerLogTailerTests()
    {
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _diagnostics = new DiagnosticStore(_store);
        _tailer = new PlayerLogTailer(new TestSessionStore(_store), _diagnostics);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// 非法 UTF-8 不能讓讀取位置漂移。
    ///
    /// <para>
    /// 舊作法用 <c>GetByteCount(解碼後的字串)</c> 回推推進量：非法序列被換成
    /// U+FFFD（3 位元組）而只消耗 1~2 個位元組，推進量因此**大於**實際讀取量，
    /// 下一次 Seek 會跳過真實內容。這個測試就是那個漂移的迴歸保護——
    /// 第三筆診斷在修正前會整行消失。
    /// </para>
    /// </summary>
    [Fact]
    public void InvalidUtf8DoesNotSkipLaterLines()
    {
        var log = Path.Combine(_root, "Player.log");

        File.WriteAllBytes(log, [
            .. Encoding.UTF8.GetBytes("error: first\n"),
            0xFF, 0xFE,
            .. Encoding.UTF8.GetBytes("\nerror: second\n"),
        ]);

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        // 追加一筆。漂移會讓下一次 Seek 落在這一行的中間，於是整行讀不到。
        File.AppendAllText(log, "error: third\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        var texts = _diagnostics.Read().Select(record => record.Text).ToList();

        Assert.Contains(texts, text => text.Contains("first", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("second", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("third", StringComparison.Ordinal));
    }

    /// <summary>
    /// 沒有結尾換行的最後一段要留到下一輪。
    /// 當成完整行分類的話，半行會變成一筆診斷，剩下的部分下一輪又變成另一筆。
    /// </summary>
    [Fact]
    public void PartialTrailingLineIsHeldUntilItIsCompleted()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, "error: complete\nwarn", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        var afterFirstRead = _diagnostics.Read().Select(record => record.Text).ToList();

        Assert.Contains(afterFirstRead, text => text.Contains("complete", StringComparison.Ordinal));
        Assert.DoesNotContain(afterFirstRead, text => text.Contains("warn", StringComparison.Ordinal));

        File.AppendAllText(log, "ing: partial\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("warning: partial", StringComparison.Ordinal));
    }

    /// <summary>
    /// 遊戲重啟會截斷 Player.log。位置必須歸零，否則會一直停在舊的位元組位置
    /// 而什麼都讀不到。
    /// </summary>
    [Fact]
    public void TruncatedLogIsReadFromTheStart()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, new string('x', 4096) + "\nerror: old\n", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        // 遊戲重啟：檔案被截斷，新的內容比舊的 offset 還短。
        File.WriteAllText(log, "error: after restart\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("after restart", StringComparison.Ordinal));
    }

    /// <summary>BOM 不影響讀取，而且第一行仍必須被分類（Classify 會剝掉它）。</summary>
    [Fact]
    public void ByteOrderMarkDoesNotHideTheFirstLine()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllBytes(log, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("error: with bom\n")]);

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("with bom", StringComparison.Ordinal));
    }

    /// <summary>只讀「這次測試新增」的內容：LogOffset 之前的不算。</summary>
    [Fact]
    public void ContentBeforeLogOffsetIsNotReported()
    {
        var log = Path.Combine(_root, "Player.log");
        var prefix = "error: from the previous run\n";
        File.WriteAllText(log, prefix + "error: this run\n", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log, logOffset: Encoding.UTF8.GetByteCount(prefix)), offsets);

        var texts = _diagnostics.Read().Select(record => record.Text).ToList();

        Assert.DoesNotContain(texts, text => text.Contains("previous run", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("this run", StringComparison.Ordinal));
    }

    /// <summary>
    /// 一個例外的堆疊要算**一筆**診斷，不是每一行一筆。
    ///
    /// <para>
    /// 一個例外在 Player.log 裡通常有 5~20 行堆疊。逐行各成一筆會把保留容量
    ///（200 筆）瞬間塞滿、把真正的錯誤擠掉，同時把 error_count 灌成假的高數字。
    /// </para>
    /// </summary>
    [Fact]
    public void StackTraceContinuationLinesMergeIntoThePrecedingDiagnostic()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "Exception in Thing.Tick: boom",
            "  at Verse.Thing.Tick()",
            "  at Verse.Thing.TickLong()",
            "at System.Object.GetType()",
        ]);

        var entry = Assert.Single(batch);

        Assert.Equal("error", entry.Type);
        Assert.Equal("Exception in Thing.Tick: boom", entry.FirstLine);
        Assert.Contains("at Verse.Thing.Tick()", entry.Text, StringComparison.Ordinal);
        Assert.Contains("at System.Object.GetType()", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 孤立的堆疊行（緊接的前一行不是診斷）要丟棄，不能誤併到更早的紀錄——
    /// 那會讓不相關的堆疊混進一筆診斷，去重簽章也跟著失真。
    /// </summary>
    [Fact]
    public void OrphanStackTraceLinesAreDroppedInsteadOfMergingIntoAnEarlierEntry()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "error: real",
            "some unrelated info line",
            "  at Orphan.Method()",
        ]);

        var entry = Assert.Single(batch);

        Assert.Equal("error: real", entry.Text);
        Assert.DoesNotContain("Orphan", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>兩個例外各自成筆，堆疊不會跨過空行或中斷行混在一起。</summary>
    [Fact]
    public void TwoSeparateExceptionsStaySeparate()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "error: first",
            "  at A.B()",
            string.Empty,
            "warning: second",
            "  at C.D()",
        ]);

        Assert.Equal(2, batch.Count);
        Assert.Equal("error", batch[0].Type);
        Assert.Contains("at A.B()", batch[0].Text, StringComparison.Ordinal);
        Assert.Equal("warning", batch[1].Type);
        Assert.Contains("at C.D()", batch[1].Text, StringComparison.Ordinal);
    }

    private static TestSession Session(string playerLog, long logOffset = 0) => new()
    {
        State = "running",
        RunId = "run-1",
        PlayerLog = playerLog,
        LogOffset = logOffset,
    };
}
