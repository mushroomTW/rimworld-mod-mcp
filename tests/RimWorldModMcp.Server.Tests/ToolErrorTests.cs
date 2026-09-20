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

    /// <summary>
    /// 建立目錄連結／junction 失敗時拋的是 Win32Exception（防毒干擾、權限不足、
    /// 路徑過長）。它的訊息含 Win32 錯誤碼，是可操作的線索——不轉換的話
    /// 呼叫端只看到「An error occurred」，而那正是使用者最需要線索的失敗之一。
    /// </summary>
    [Fact]
    public void PlatformApiFailuresAreReportedWithTheirWin32Message()
    {
        var error = Assert.Throws<McpException>(
            () => ToolGuard.Run<int>(() => throw new System.ComponentModel.Win32Exception(5, "Access is denied.")));

        Assert.Contains("Access is denied", error.Message);
    }

    /// <summary>
    /// dotnet build 逾時與搜尋模式逾時都必須送達。<c>RegexMatchTimeoutException</c>
    /// 繼承 <c>TimeoutException</c>，所以兩者走同一條路徑——這個測試同時守住那個繼承關係。
    /// </summary>
    [Fact]
    public void TimeoutsAreReported()
    {
        var build = Assert.Throws<McpException>(
            () => ToolGuard.Run<int>(() => throw new TimeoutException("dotnet build did not finish within 10 minutes and was terminated.")));

        Assert.Contains("did not finish within", build.Message);

        Assert.Throws<McpException>(
            () => ToolGuard.Run<int>(() => throw new System.Text.RegularExpressions.RegexMatchTimeoutException("a+", "aaa", TimeSpan.FromSeconds(5))));
    }

    /// <summary>非預期的例外不能被包成 McpException，否則程式錯誤會被誤當成使用者問題。</summary>
    [Fact]
    public void UnexpectedExceptionsAreNotMasked()
    {
        Assert.Throws<NullReferenceException>(
            () => ToolGuard.Run<int>(() => throw new NullReferenceException("programming error")));

        Assert.Throws<InvalidCastException>(
            () => ToolGuard.Run<int>(() => throw new InvalidCastException("programming error")));
    }
}
