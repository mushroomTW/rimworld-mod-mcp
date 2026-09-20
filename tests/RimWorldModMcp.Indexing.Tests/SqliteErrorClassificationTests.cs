using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// SQLite 錯誤碼的分類。這個判定同時驅動三件不同的事，寫錯的後果各不相同：
/// 損毀要指向 rebuild_index、忙碌要說「稍後重試」、而 Mod 背景索引
/// **不能**把忙碌記成永久失敗（記了就再也不會自動重試）。
/// </summary>
public sealed class SqliteErrorClassificationTests
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;
    private const int SqliteReadOnly = 8;

    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    public void BusyAndLockedAreTransient(int code)
    {
        var error = new SqliteException("database is locked", code);

        Assert.True(SqliteCorruption.IsTransientBusy(error));
        Assert.False(SqliteCorruption.IsCorrupt(error));
    }

    [Theory]
    [InlineData(SqliteCorrupt)]
    [InlineData(SqliteNotADb)]
    public void CorruptAndNotADbAreCorruption(int code)
    {
        var error = new SqliteException("file is not a database", code);

        Assert.True(SqliteCorruption.IsCorrupt(error));
        Assert.False(SqliteCorruption.IsTransientBusy(error));
    }

    /// <summary>
    /// 兩類必須互斥。一個錯誤若同時被當成損毀與忙碌，rebuild_index 的重試路徑
    /// 與 Mod 索引的「不要記成永久失敗」路徑就會對同一個錯誤做出矛盾的反應。
    /// </summary>
    [Theory]
    [InlineData(SqliteBusy)]
    [InlineData(SqliteLocked)]
    [InlineData(SqliteCorrupt)]
    [InlineData(SqliteNotADb)]
    [InlineData(SqliteReadOnly)]
    [InlineData(1)]
    public void TheTwoClassificationsNeverOverlap(int code)
    {
        var error = new SqliteException("message", code);

        Assert.False(SqliteCorruption.IsCorrupt(error) && SqliteCorruption.IsTransientBusy(error));
    }

    /// <summary>唯讀（8）既不是損毀也不是忙碌——它需要的是換路徑或改權限，不是重試。</summary>
    [Fact]
    public void ReadOnlyIsNeitherCorruptNorBusy()
    {
        var error = new SqliteException("attempt to write a readonly database", SqliteReadOnly);

        Assert.False(SqliteCorruption.IsCorrupt(error));
        Assert.False(SqliteCorruption.IsTransientBusy(error));
    }
}
