using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using RimWorldModMcp.Server.Tools;

namespace RimWorldModMcp.Server.Tests;

/// <summary>
/// 錯誤訊息是給 LLM 呼叫端自我修正用的：拋錯的「位置」與「型別」決定它到底看不看得到。
/// </summary>
public sealed class ToolErrorTests : IAsyncLifetime
{
    private McpServerFixture _server = null!;

    public async Task InitializeAsync() => _server = await McpServerFixture.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    /// <summary>參數驗證拋在 ToolGuard 之外時，呼叫端只會看到「An error occurred」。</summary>
    [Fact]
    public async Task InvalidSymbolKindIsReportedByName()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.CallAsync("list_symbols", new Dictionary<string, object?> { ["parent"] = "", ["kind"] = "Bogus" }));

        Assert.Contains("Unknown symbol kind: Bogus", error.Message);
    }

    /// <summary>索引損毀時每個查詢工具都會失敗；訊息必須指向 rebuild_index。</summary>
    [Theory]
    [InlineData(11)] // SQLITE_CORRUPT
    [InlineData(26)] // SQLITE_NOTADB
    public void CorruptIndexTellsTheCallerToRebuild(int sqliteErrorCode)
    {
        var error = Assert.Throws<McpException>(
            () => ToolGuard.Run<int>(() => throw new SqliteException("database disk image is malformed", sqliteErrorCode)));

        Assert.Contains("corrupt", error.Message);
        Assert.Contains("rebuild_index", error.Message);
    }

    [Fact]
    public void BusyIndexTellsTheCallerToRetry()
    {
        var error = Assert.Throws<McpException>(
            () => ToolGuard.Run<int>(() => throw new SqliteException("database is locked", 5)));

        Assert.Contains("Retry", error.Message);
    }

    /// <summary>其他 SQLite 錯誤是程式問題，維持原樣讓它進 stderr 日誌。</summary>
    [Fact]
    public void OtherSqliteErrorsAreNotMasked()
    {
        Assert.Throws<SqliteException>(
            () => ToolGuard.Run<int>(() => throw new SqliteException("no such table: nope", 1)));
    }
}
