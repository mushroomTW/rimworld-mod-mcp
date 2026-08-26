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
}
