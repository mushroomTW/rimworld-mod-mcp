using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 索引資料庫檔案的生命週期：損毀偵測、殘留 WAL、刪檔順序。
/// 這些在實機上表現為「所有查詢工具都回 An error occurred」，值得直接鎖住。
/// </summary>
public sealed class IndexDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rwmm-index-db-" + Guid.NewGuid().ToString("n")[..12]);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private IndexDatabase NewDatabase() => new(new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache")));

    [Fact]
    public void CorruptDatabaseIsResetOnFirstOpen()
    {
        var database = NewDatabase();

        using (var connection = database.Open())
        {
            IndexMetaRepository.Set(connection, "fingerprint", "before");
        }

        SqliteConnection.ClearAllPools();

        // 把檔頭以外的內容砍掉：SQLite 會回 SQLITE_NOTADB／SQLITE_CORRUPT。
        using (var file = new FileStream(database.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            file.SetLength(100);
        }

        File.Delete(database.DatabasePath + "-wal");
        File.Delete(database.DatabasePath + "-shm");

        // 新的行程（新的 IndexDatabase 實例）第一次開啟就該拿到乾淨、可用的資料庫。
        using var fresh = NewDatabase().Open();
        Assert.Null(IndexMetaRepository.Get(fresh, "fingerprint"));
        Assert.Equal(0, DefRepository.Count(fresh));
    }

    [Fact]
    public void ResetRemovesEveryFileAndTheNextOpenStartsClean()
    {
        var database = NewDatabase();

        using (var connection = database.Open())
        {
            IndexMetaRepository.Set(connection, "fingerprint", "old");
        }

        database.Reset();

        Assert.False(File.Exists(database.DatabasePath));
        Assert.False(File.Exists(database.DatabasePath + "-wal"));
        Assert.False(File.Exists(database.DatabasePath + "-shm"));

        using var fresh = database.Open();
        Assert.Null(IndexMetaRepository.Get(fresh, "fingerprint"));
    }
}
