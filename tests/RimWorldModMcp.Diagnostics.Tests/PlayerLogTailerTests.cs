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
            .. Encoding.UTF8.GetBytes("2026-10-13 01:29:00.100 ERROR [Verse] first\n"),
            0xFF, 0xFE,
            .. Encoding.UTF8.GetBytes("\n2026-10-13 01:29:00.200 ERROR [Verse] second\n"),
        ]);

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        // 追加一筆。漂移會讓下一次 Seek 落在這一行的中間，於是整行讀不到。
        File.AppendAllText(log, "2026-10-13 01:29:00.300 ERROR [Verse] third\n", new UTF8Encoding(false));
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
        File.WriteAllText(log, "2026-10-13 01:29:00.100 ERROR [Verse] complete\n2026-10-13 01:29:00.200  WARN [Verse] warn", new UTF8Encoding(false));

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
        File.WriteAllText(log, new string('x', 4096) + "\n2026-10-13 01:29:00.100 ERROR [Verse] old\n", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        // 遊戲重啟：檔案被截斷，新的內容比舊的 offset 還短。
        File.WriteAllText(log, "2026-10-13 01:29:00.200 ERROR [Verse] after restart\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("after restart", StringComparison.Ordinal));
    }

    /// <summary>BOM 不影響讀取，而且第一行仍必須被分類（Normalise 會剝掉它）。</summary>
    [Fact]
    public void ByteOrderMarkDoesNotHideTheFirstLine()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllBytes(log, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("2026-10-13 01:29:00.100 ERROR [Verse] with bom\n")]);

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("with bom", StringComparison.Ordinal));
    }

    /// <summary>
    /// 遊戲啟動時把 Player.log 改名成 Player-prev.log 再建新檔。Windows 的檔案通道效應
    /// 讓新檔沿用舊檔的建立時間，建立時間判斷失效；新檔在第一次輪詢前就長得比 LogOffset 長時，
    /// 新檔開頭（XML 載入錯誤正好在這裡）會被當成「上一場的內容」跳過。
    /// 以 Player-prev.log 的戳記變化判定輪替。
    /// </summary>
    [Fact]
    public void RotationBeforeTheFirstReadStartsFromTheBeginningOfTheNewLog()
    {
        var log = Path.Combine(_root, "Player.log");
        var old = "old\n";
        File.WriteAllText(log, old, new UTF8Encoding(false));

        var session = Session(log, logOffset: Encoding.UTF8.GetByteCount(old)) with
        {
            PreviousLogStamp = PlayerLogTailer.PreviousLogStamp(log),
        };

        File.Move(log, Path.Combine(_root, "Player-prev.log"));
        File.WriteAllText(log, "2026-10-13 01:29:00.100 ERROR [Verse] early startup failure\nloading...\n", new UTF8Encoding(false));

        _tailer.ReadNewLines(session, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("early startup", StringComparison.Ordinal));
    }

    /// <summary>只讀「這次測試新增」的內容：LogOffset 之前的不算。</summary>
    [Fact]
    public void ContentBeforeLogOffsetIsNotReported()
    {
        var log = Path.Combine(_root, "Player.log");
        var prefix = "2026-10-13 01:29:00.100 ERROR [Verse] from the previous run\n";
        File.WriteAllText(log, prefix + "2026-10-13 01:29:00.200 ERROR [Verse] this run\n", new UTF8Encoding(false));

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
            "2026-10-13 01:29:00.300 ERROR [Verse] Exception in Thing.Tick: boom",
            "  at Verse.Thing.Tick()",
            "  at Verse.Thing.TickLong()",
            "at System.Object.GetType()",
            "    at Verse.Log.Error(String text) [0x1A] in Log.cs:137",
        ]);

        var entry = Assert.Single(batch);

        Assert.Equal("error", entry.Type);
        Assert.Equal("Exception in Thing.Tick: boom", entry.FirstLine);
        Assert.Contains("at Verse.Thing.Tick()", entry.Text, StringComparison.Ordinal);
        Assert.Contains("at System.Object.GetType()", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Log.Error", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 孤立的堆疊行（所屬紀錄不是診斷，或前面根本沒有紀錄）要丟棄，不能誤併到更早的紀錄——
    /// 那會讓不相關的堆疊混進一筆診斷，去重簽章也跟著失真。
    /// </summary>
    [Fact]
    public void OrphanStackTraceLinesAreDroppedInsteadOfMergingIntoAnEarlierEntry()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "  at Header.Orphan()",
            "2026-10-13 01:29:00.100 ERROR [Verse] real",
            "2026-10-13 01:29:00.200  INFO [Verse] some unrelated info line",
            "  at Orphan.Method()",
        ]);

        var entry = Assert.Single(batch);

        Assert.Equal("real", entry.Text);
        Assert.DoesNotContain("Orphan", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>帶效能標記的 Log.Message 要被收成 performance，且標記優先於層級與 error 字樣；沒標記的 INFO 仍被忽略。</summary>
    [Fact]
    public void PerformanceMarkedLinesAreClassifiedAsPerformance()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.100  INFO [Verse] [perf] Round 1 timing: mainThreadReadAllBytes=41ms",
            "2026-10-13 01:29:00.200  INFO [Verse] [PERF] startup with 0 errors",
            "2026-10-13 01:29:00.300  INFO [Verse] Round 2 timing: plain message without marker",
        ]);

        Assert.Equal(2, batch.Count);
        Assert.All(batch, entry => Assert.Equal("performance", entry.Type));
    }

    /// <summary>環境變數追加的標記以 | 分隔，空白與空項目會被忽略；預設標記永遠存在。</summary>
    [Fact]
    public void ExtraPerformanceMarkersAreAppendedToTheDefault()
    {
        var markers = PerformanceMarkers.Load(" [FGL InGameTests] | |timing]");

        Assert.Equal(["[perf]", "[FGL InGameTests]", "timing]"], markers);
        Assert.True(PerformanceMarkers.Matches("[FGL InGameTests] Round 1 timing", markers));
        Assert.False(PerformanceMarkers.Matches("[FGL InGameTests] Round 1 timing", PerformanceMarkers.Load(null)));
    }

    /// <summary>兩個例外各自成筆，堆疊不會跨過空行或下一筆紀錄混在一起。</summary>
    [Fact]
    public void TwoSeparateExceptionsStaySeparate()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.100 ERROR [Verse] first",
            "  at A.B()",
            string.Empty,
            "2026-10-13 01:29:00.200  WARN [Verse] second",
            "  at C.D()",
        ]);

        Assert.Equal(2, batch.Count);
        Assert.Equal("error", batch[0].Type);
        Assert.Contains("at A.B()", batch[0].Text, StringComparison.Ordinal);
        Assert.Equal("warning", batch[1].Type);
        Assert.Contains("at C.D()", batch[1].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 1.7 的日誌每行帶層級：照層級分類，而不是關鍵字。「 WARN」不含 warning 字樣，
    /// 靠關鍵字會整個漏掉；含 error 字樣的 INFO 也不能算成錯誤。
    /// </summary>
    [Fact]
    public void DotNetLogLinesAreClassifiedByTheirLevel()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "=== RimWorld by Ludeon Studios log ===",
            "Command:   RimWorld.exe -savedatafolder=C:/tmp/run",
            "2026-10-13 01:29:00.100  INFO [Verse] Loaded 0 errors from cache",
            "2026-10-13 01:29:00.200  WARN [Verse] Something odd",
            "2026-10-13 01:29:00.300 ERROR [Verse] [MyMod] failed",
            "2026-10-13 01:29:00.400  CRIT [Waylith] device lost",
        ]);

        Assert.Equal(3, batch.Count);
        Assert.Equal(("warning", "Something odd"), (batch[0].Type, batch[0].FirstLine));
        Assert.Equal(("error", "[MyMod] failed"), (batch[1].Type, batch[1].FirstLine));
        Assert.Equal(("error", "device lost"), (batch[2].Type, batch[2].FirstLine));
    }

    /// <summary>
    /// 時間戳記與分類要剝掉：同一個錯誤每次發生時間不同，留著的話去重簽章全都不同，
    /// 也對不上 Bridge 回報的原始訊息。
    /// </summary>
    [Fact]
    public void RepeatedDotNetErrorsShareTheSameText()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.300 ERROR [Verse] boom",
            "2026-10-13 01:29:05.900 ERROR [Verse] boom",
        ]);

        Assert.Equal(2, batch.Count);
        Assert.Equal(batch[0].Text, batch[1].Text);
        Assert.Equal("boom", batch[0].Text);
    }

    /// <summary>
    /// 1.7 的一筆紀錄可跨多行（多行訊息、四格縮排的堆疊），直到下一個帶層級的行為止。
    /// 錯誤的後續行全部併入；INFO 的後續行即使含 exception 字樣也不能被當成另一筆錯誤。
    /// </summary>
    [Fact]
    public void DotNetRecordContinuationLinesBelongToTheirRecord()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.300 ERROR [Verse] Exception ticking Thing",
            "System.NullReferenceException: Object reference not set",
            "    at Verse.Thing.Tick() [0x1A]",
            "2026-10-13 01:29:00.400  INFO [Verse] Dumping state",
            "previous exception was handled",
            "    at Verse.Root.Update() [0x0]",
        ]);

        var entry = Assert.Single(batch);

        Assert.Equal("Exception ticking Thing", entry.FirstLine);
        Assert.Contains("System.NullReferenceException", entry.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Root.Update", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 引擎替每筆紀錄附的呼叫端堆疊不能進診斷：去重簽章只看 at 行，同一呼叫點的不同錯誤
    /// 會被併成一筆，也對不上不帶堆疊的 Bridge 回報。訊息本身的例外堆疊（沒有 [0x..]）要留著。
    /// </summary>
    [Fact]
    public void DotNetEngineStackFramesAreNotPartOfTheDiagnostic()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, string.Join('\n',
            "2026-10-13 01:29:00.300 ERROR [Verse] Could not resolve cross-reference to A",
            "    at Verse.Log.Error(String text) [0x1A] in Log.cs:137",
            "2026-10-13 01:29:00.400 ERROR [Verse] Could not resolve cross-reference to B",
            "    at Verse.Log.Error(String text) [0x1A] in Log.cs:137",
            "2026-10-13 01:29:00.500 ERROR [Verse] Exception in tick: System.Exception: boom",
            "   at MyMod.Comp.Tick() in Comp.cs:line 12",
            "    at Verse.Log.Error(String text) [0x1A] in Log.cs:137",
            string.Empty), new UTF8Encoding(false));

        _tailer.ReadNewLines(Session(log), new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

        var texts = _diagnostics.Read().Select(record => record.Text).ToList();

        Assert.Equal(3, texts.Count);
        Assert.Contains("Could not resolve cross-reference to A", texts);
        Assert.Contains("Could not resolve cross-reference to B", texts);
        Assert.Contains("Exception in tick: System.Exception: boom\nat MyMod.Comp.Tick() in Comp.cs:line 12", texts);
    }

    /// <summary>
    /// 輪詢邊界切在 1.7 紀錄中間：下一批開頭的行屬於上一批尾那一筆。
    /// 是 INFO 就丟掉，不能併到更早的錯誤；是錯誤就併進去，不能被關鍵字分類成另一筆。
    /// </summary>
    [Fact]
    public void DotNetRecordSplitAcrossPollsStaysWithItsRecord()
    {
        var log = Path.Combine(_root, "Player.log");
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        File.WriteAllText(log, "2026-10-13 01:29:00.300 ERROR [Verse] boom\n2026-10-13 01:29:00.400  INFO [Verse] Dumping state\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        File.AppendAllText(log, "previous exception was handled\n    at Verse.Root.Update() [0x0]\n2026-10-13 01:29:00.500 ERROR [Verse] second\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        File.AppendAllText(log, "System.NullReferenceException: Object reference not set\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        var records = _diagnostics.Read();

        Assert.Equal(2, records.Count);
        Assert.Equal("boom", records.Single(record => record.FirstLine == "boom").Text);
        Assert.Equal("second\nSystem.NullReferenceException: Object reference not set", records.Single(record => record.FirstLine == "second").Text);
    }

    /// <summary>
    /// 訊息以換行開頭時標題行沒有內容：第一個續行就是訊息，不能因為標題空白就整筆丟掉。
    /// 標題空白、後面也沒有續行的紀錄沒有可回報的文字，不產生診斷。
    /// </summary>
    [Fact]
    public void ErrorWhoseMessageStartsWithANewlineUsesItsFirstContinuationLine()
    {
        var batch = PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.300 ERROR [Verse] ",
            "[MyMod] failed to load X",
            "    at Verse.Log.Error(String text) [0x1A] in Log.cs:137",
            "2026-10-13 01:29:00.400 ERROR [Verse]",
        ], out var trailingRecordIsEntry);

        var entry = Assert.Single(batch);

        Assert.Equal(("error", "[MyMod] failed to load X", "[MyMod] failed to load X"), entry);
        Assert.False(trailingRecordIsEntry);
    }

    /// <summary>1.7 的時間以文化格式化，時間分隔符號不一定是冒號（例如 fi-FI 用句點）。</summary>
    [Fact]
    public void DotNetLinesWithANonColonTimeSeparatorAreStillStructured()
    {
        var entry = Assert.Single(PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01.29.00.200  WARN [Verse] Something odd",
        ]));

        Assert.Equal(("warning", "Something odd"), (entry.Type, entry.FirstLine));
    }

    /// <summary>效能標記優先於層級：帶標記的 INFO 也要收成 performance。</summary>
    [Fact]
    public void PerformanceMarkedDotNetLinesAreClassifiedAsPerformance()
    {
        var entry = Assert.Single(PlayerLogTailer.ClassifyBatch([
            "2026-10-13 01:29:00.100  INFO [Verse] [perf] Round 1 timing: 41ms",
        ]));

        Assert.Equal(("performance", "[perf] Round 1 timing: 41ms"), (entry.Type, entry.FirstLine));
    }

    /// <summary>
    /// F08：日誌重建且快速長大時不可漏掉開頭的錯誤。
    /// 僅以 length &lt; offset 判斷換檔時，重建後的新檔若已超過舊 offset，
    /// 會被誤認為原檔而直接跳過開頭。
    /// </summary>
    [Fact]
    public void RecreatedLogLongerThanTheOldOffsetIsReadFromTheStart()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, new string('x', 60) + "\n2026-10-13 01:29:00.100 ERROR [Verse] old\n", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        // 重建（刪除後重建，新檔開頭即錯誤，且總長度已超過舊 offset）。
        File.Delete(log);
        File.WriteAllText(
            log,
            "2026-10-13 01:29:00.200 ERROR [Verse] early failure\n" + new string('y', 200) + "\n",
            new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        Assert.Contains(
            _diagnostics.Read().Select(record => record.Text),
            text => text.Contains("early failure", StringComparison.Ordinal));
    }

    /// <summary>
    /// F09：錯誤標題與堆疊分處兩輪輪詢時，堆疊不可遺失。
    /// 上一批尾是診斷、本批頭是不帶層級的續行（`at ...`）時，必須併入同一筆。
    /// </summary>
    [Fact]
    public void StackTraceAcrossPollsIsMergedIntoTheSameDiagnostic()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, "2026-10-13 01:29:00.100 ERROR [Verse] failed\n", new UTF8Encoding(false));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        File.AppendAllText(log, "  at Example.Work()\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        var entry = Assert.Single(_diagnostics.Read());
        Assert.Contains("Example.Work()", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Linux 上 .NET 的 CreationTimeUtc 在沒有真正建立時間可用時取自 mtime／ctime，
    /// 追加寫入就會改變它。若拿來判斷檔案身分，每次追加都被當成重建而從頭重讀，
    /// 同一筆錯誤重複回報、跨輪詢的堆疊也接不上。檔案時間戳粒度粗，追加若落在同一刻度
    /// 會碰巧沒事，所以這裡先把修改時間往前調，讓追加必然改變 Linux 上的建立時間。
    /// </summary>
    [Fact]
    public void AppendAfterTheTimestampTickDoesNotRestartReading()
    {
        var log = Path.Combine(_root, "Player.log");
        File.WriteAllText(log, "2026-10-13 01:29:00.100 ERROR [Verse] failed\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddHours(-1));

        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        _tailer.ReadNewLines(Session(log), offsets);

        File.AppendAllText(log, "  at Example.Work()\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(Session(log), offsets);

        var entry = Assert.Single(_diagnostics.Read());
        Assert.Contains("Example.Work()", entry.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 場次開始前就存在、場次中持續被追加的 Player.log 不是「本場次建立的新檔」。
    /// Linux 上建立時間會隨追加更新，若據此判斷就會把 LogOffset 之前（上一場）的內容當成本場診斷。
    /// </summary>
    [Fact]
    public void LogAppendedDuringTheSessionIsNotMistakenForANewFile()
    {
        var log = Path.Combine(_root, "Player.log");
        var previous = "2026-10-13 01:29:00.100 ERROR [Verse] from the previous run\n";
        File.WriteAllText(log, previous, new UTF8Encoding(false));

        var longAgo = DateTime.UtcNow.AddHours(-1);
        File.SetCreationTimeUtc(log, longAgo);
        File.SetLastWriteTimeUtc(log, longAgo);

        var session = Session(log, logOffset: Encoding.UTF8.GetByteCount(previous)) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds(),
        };

        File.AppendAllText(log, "2026-10-13 01:29:00.200 ERROR [Verse] this run\n", new UTF8Encoding(false));
        _tailer.ReadNewLines(session, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

        var texts = _diagnostics.Read().Select(record => record.Text).ToList();

        Assert.DoesNotContain(texts, text => text.Contains("previous run", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("this run", StringComparison.Ordinal));
    }

    private static TestSession Session(string playerLog, long logOffset = 0) => new()
    {
        State = "running",
        RunId = "run-1",
        PlayerLog = playerLog,
        LogOffset = logOffset,
    };
}
