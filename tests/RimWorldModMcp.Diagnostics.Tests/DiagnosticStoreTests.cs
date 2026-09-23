using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

public sealed class DiagnosticStoreTests : IDisposable
{
    private readonly string _root;
    private readonly DiagnosticStore _diagnostics;

    public DiagnosticStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-diag-" + Guid.NewGuid().ToString("n")[..12]);
        var store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _diagnostics = new DiagnosticStore(store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AddRangeMergesDuplicatesWithinTheBatch()
    {
        _diagnostics.AddRange(
        [
            ("error", "boom", "boom\n  at Verse.Thing.Tick()"),
            ("error", "boom", "boom\n  at Verse.Thing.Tick()"),
        ], "player.log", "run-1");

        var records = _diagnostics.Read();

        Assert.Single(records);
        Assert.Equal(2, records[0].Count);
    }

    /// <summary>
    /// 同一個例外 Bridge 與 Player.log 各送一次：Bridge 的堆疊行帶縮排，Player.log 的已被 trim。
    /// 兩者必須併成一筆，error_count 只算一次，count 也不被另一個來源重複累加。
    /// </summary>
    [Fact]
    public void SameExceptionFromBridgeAndPlayerLogIsCountedOnce()
    {
        _diagnostics.Add("error", "Exception ticking X: boom",
            "Exception ticking X: boom\n  at Verse.Thing.Tick () [0x00000] in <abc>:0\n  at Verse.TickList.Tick () [0x00000] in <abc>:0",
            "bridge", "run-1");

        _diagnostics.AddRange(
        [
            ("error", "Exception ticking X: boom",
                "Exception ticking X: boom\nat Verse.Thing.Tick () [0x00000] in <abc>:0\nat Verse.TickList.Tick () [0x00000] in <abc>:0"),
        ], "player.log", "run-1");

        var record = Assert.Single(_diagnostics.Read());
        Assert.Equal(1, record.Count);
        Assert.Equal("bridge", record.Source);
        Assert.Equal(1, _diagnostics.Totals.Error);
    }

    [Fact]
    public void ReadSinceReturnsNewAndReoccurringRecordsOnly()
    {
        _diagnostics.Add("error", "old", "old failure", "bridge", "run-1");
        _diagnostics.Add("warning", "untouched", "before cursor", "bridge", "run-1");

        var last = _diagnostics.Read().MaxBy(r => (r.At, r.Sequence))!;
        var cursor = last.At;
        var sequence = last.Sequence;

        Thread.Sleep(5);
        _diagnostics.Add("error", "new", "new failure", "bridge", "run-1");
        _diagnostics.Add("error", "old", "old failure", "bridge", "run-1");

        var since = _diagnostics.ReadSince(cursor, sequence);

        // 「old」重複出現而 count 變 2，也要回；純粹在游標前的不回。
        Assert.Equal(["old failure", "new failure"], since.Select(r => r.Text).ToArray());
        Assert.Empty(_diagnostics.ReadSince(long.MaxValue));
    }

    /// <summary>
    /// 容量淘汰必須按「最久未更新」而不是 list 位置——按位置砍的話，
    /// 出現最頻繁的錯誤（最早進來、持續被更新）反而最先被丟掉。
    /// </summary>
    [Fact]
    public void FrequentlyUpdatedRecordSurvivesEviction()
    {
        var fill = Enumerable.Range(0, 200)
            .Select(i => ("error", $"failure {i}", $"failure number {i}"))
            .ToList();

        _diagnostics.AddRange(fill, "bridge", "run-1");

        // 第 0 筆再次出現：它是最「新鮮」的紀錄。
        var updated = _diagnostics.Add("error", "failure 0", "failure number 0", "bridge", "run-1");

        // 塞入新紀錄擠掉舊的。
        _diagnostics.AddRange(
            Enumerable.Range(0, 5).Select(i => ("error", $"late {i}", $"late failure {i}")).ToList(),
            "bridge",
            "run-1");

        var records = _diagnostics.Read();

        Assert.Equal(200, records.Count);
        Assert.Contains(records, r => r.Hash == updated.Hash);
    }

    [Fact]
    public void OversizedTextIsCappedOnWrite()
    {
        var huge = new string('x', 200_000);

        _diagnostics.Add("error", "big", huge, "bridge", "run-1");

        Assert.True(_diagnostics.Read()[0].Text.Length <= 64 * 1024);
    }

    [Fact]
    public void ClearEmptiesTheStore()
    {
        _diagnostics.Add("error", "boom", "boom", "bridge", "run-1");
        _diagnostics.Clear();

        Assert.Empty(_diagnostics.Read());
    }

    /// <summary>
    /// 累計數不能在淘汰之後下降。
    ///
    /// <para>
    /// 保留容量是 200 筆。crash loop 會在幾秒內產生遠多於此的**不同**錯誤並把紀錄擠掉。
    /// 若 error_count 是「數保留中的紀錄」，輪詢中的 agent 會看到它從 300 掉回 200、
    /// 甚至掉到 0，然後據此判定「測試無錯誤」——而那是錯的。
    /// </para>
    /// </summary>
    [Fact]
    public void TotalsDoNotDecreaseWhenRecordsAreEvicted()
    {
        for (var i = 0; i < 300; i++)
        {
            _diagnostics.Add("error", $"error {i}", $"error {i}", "bridge", "run-1");
        }

        Assert.Equal(300, _diagnostics.Totals.Error);
        Assert.Equal(0, _diagnostics.Totals.Warning);

        // 保留中的紀錄被容量上限壓住了，但累計數不受影響。
        Assert.Equal(200, _diagnostics.Read().Count);
    }

    /// <summary>同一個簽章重複出現時累計只算一次——它是一筆診斷，只是次數變多。</summary>
    [Fact]
    public void RepeatedIdenticalDiagnosticsDoNotInflateTheTotal()
    {
        for (var i = 0; i < 50; i++)
        {
            _diagnostics.Add("error", "boom", "boom\n  at Verse.Thing.Tick()", "bridge", "run-1");
        }

        Assert.Equal(1, _diagnostics.Totals.Error);
        Assert.Equal(50, _diagnostics.Read()[0].Count);
    }

    [Fact]
    public void TotalsCountErrorsAndWarningsSeparately()
    {
        _diagnostics.Add("error", "e", "e", "bridge", "run-1");
        _diagnostics.Add("warning", "w", "w", "bridge", "run-1");
        _diagnostics.Add("diagnostic", "d", "d", "bridge", "run-1");
        _diagnostics.Add("performance", "p", "p", "bridge", "run-1");

        Assert.Equal(1, _diagnostics.Totals.Error);
        Assert.Equal(1, _diagnostics.Totals.Warning);
    }

    /// <summary>累計數屬於「這一場」，換場次必須歸零。</summary>
    [Fact]
    public void ClearResetsTheTotals()
    {
        _diagnostics.Add("error", "boom", "boom", "bridge", "run-1");
        _diagnostics.Clear();

        Assert.Equal(0, _diagnostics.Totals.Error);
        Assert.Equal(0, _diagnostics.Totals.Warning);
    }
}
