using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>
/// 索引資料庫檔案的生命週期：損毀偵測與 Reset。
/// 損毀時所有查詢工具都會回「An error occurred」，這裡鎖住「重開就自動重建」
/// 的行為；Reset 則驗證刪檔與下次開啟從乾淨狀態開始。
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

    /// <summary>全新的空白檔（沒有表、user_version = 0）必須被接受，走正常的建結構路徑。</summary>
    [Fact]
    public void BrandNewDatabaseIsAcceptedAndInitialised()
    {
        using var connection = NewDatabase().Open();

        Assert.Equal(IndexSchema.Version, ReadUserVersion(connection));
        Assert.True(TableExists(connection, "def"));
        Assert.True(TableExists(connection, "source_fts"));
    }

    /// <summary>
    /// 有資料表卻沒有版本戳記的舊庫**不能**放行。
    ///
    /// <para>
    /// 放行的話，<c>CREATE TABLE IF NOT EXISTS</c> 不會修正既有的表，
    /// <c>WriteUserVersion</c> 卻會把它蓋成現行版本，之後才在執行期以
    /// "no such column" 失敗——而那個錯誤既不指向損毀也不指向忙碌，
    /// 呼叫端只會看到「An error occurred」。
    /// </para>
    /// </summary>
    [Fact]
    public void DatabaseWithTablesButNoVersionStampIsRebuilt()
    {
        var database = NewDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);

        WriteLegacyDatabase(database.DatabasePath, "CREATE TABLE legacy_only (id INTEGER PRIMARY KEY);", userVersion: 0);

        using var fresh = NewDatabase().Open();

        Assert.Equal(IndexSchema.Version, ReadUserVersion(fresh));
        Assert.False(TableExists(fresh, "legacy_only"));
        Assert.True(TableExists(fresh, "def"));
    }

    /// <summary>結構版本不符時整個丟掉重建——索引是衍生資料，不需要 migration。</summary>
    [Fact]
    public void DatabaseWithAnOlderSchemaVersionIsRebuilt()
    {
        var database = NewDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);

        // 只建一張殘缺的 def（只有 id 欄），模擬舊結構。
        WriteLegacyDatabase(database.DatabasePath, "CREATE TABLE def (id INTEGER PRIMARY KEY);", IndexSchema.Version - 1);

        using var fresh = NewDatabase().Open();

        Assert.Equal(IndexSchema.Version, ReadUserVersion(fresh));
        Assert.True(HasColumn(fresh, "def", "def_name"), "重建後應該是完整的 def 結構");
    }

    /// <summary>手工造一個「舊工具留下來的」資料庫檔。</summary>
    private static void WriteLegacyDatabase(string path, string ddl, int userVersion)
    {
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"{ddl} PRAGMA user_version = {userVersion};";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// 資料庫被別的寫者佔住時，開啟它必須**回報忙碌**而且**不能動到檔案**。
    ///
    /// <para>
    /// 實際的失敗點值得寫清楚：DELETE journal 模式下寫者只持有 RESERVED，
    /// **讀取仍然可行**，所以 <c>IsUsable</c>（user_version + quick_check）會通過；
    /// 真正吃到 SQLITE_BUSY 的是之後的 DDL（<c>IndexSchema.Apply</c> 的
    /// <c>CREATE TABLE IF NOT EXISTS</c>）。那個例外逸出 <c>EnsureInitialised</c>，
    /// 由 <see cref="IndexDatabase.Check"/> 轉成 <c>Health.Busy</c>。
    /// </para>
    /// <para>
    /// 這裡要釘住的是：忙碌**不會**被誤判成損毀（那會叫人去 rebuild），
    /// 也**不會**觸發刪檔重建。用 DELETE journal 模式才測得到——WAL 允許讀寫並行。
    /// </para>
    /// </summary>
    [Fact]
    public void BusyDatabaseIsReportedAsBusyAndNeverDeleted()
    {
        var database = NewDatabase();

        using (var connection = database.Open())
        {
            IndexMetaRepository.Set(connection, "fingerprint", "keep-me");
        }

        SqliteConnection.ClearAllPools();

        // 另一條連線握住寫鎖，模擬另一個實例正在重建。
        using var blocker = new SqliteConnection($"Data Source={database.DatabasePath}");
        blocker.Open();

        using (var journal = blocker.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            journal.ExecuteNonQuery();
        }

        using var blockerTransaction = blocker.BeginTransaction();

        using (var write = blocker.CreateCommand())
        {
            write.CommandText = "UPDATE index_meta SET value = 'busy' WHERE key = 'fingerprint';";
            write.ExecuteNonQuery();
        }

        // 新的行程（新的 IndexDatabase 實例）第一次開啟。
        var fresh = NewDatabase();

        Assert.Equal(IndexDatabase.Health.Busy, fresh.Check());
        Assert.True(File.Exists(database.DatabasePath), "忙碌時絕對不該刪掉資料庫檔");

        blockerTransaction.Rollback();

        // 放掉寫鎖之後，原本的內容必須還在——證明剛才沒有重建過。
        using var after = fresh.Open();
        Assert.Equal("keep-me", IndexMetaRepository.Get(after, "fingerprint"));
    }

    /// <summary>
    /// 等待寫鎖的逾時必須是 5 秒，而不是 Microsoft.Data.Sqlite 的預設 30 秒。
    ///
    /// <para>
    /// 這個迴歸測試有實際價值：只寫 <c>PRAGMA busy_timeout=5000</c> 是**無效**的——
    /// Microsoft.Data.Sqlite 會在每個命令執行前用 <c>CommandTimeout</c>
    /// （預設繼承自連線的 <c>DefaultTimeout</c>，30 秒）覆寫它。
    /// 差異是「一次工具呼叫卡 30~60 秒」對「卡 5~10 秒」，而前者足以讓 MCP client
    /// 逾時，使用者卻只看到「稍後重試即可」。
    /// </para>
    /// </summary>
    [Fact]
    public void BusyTimeoutIsFiveSecondsNotTheThirtySecondDefault()
    {
        var database = NewDatabase();

        using (var connection = database.Open())
        {
            IndexMetaRepository.Set(connection, "fingerprint", "x");
        }

        SqliteConnection.ClearAllPools();

        // DELETE journal 模式才讓讀者也被寫鎖擋住（WAL 允許讀寫並行）。
        using var blocker = new SqliteConnection($"Data Source={database.DatabasePath}");
        blocker.Open();

        using (var journal = blocker.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            journal.ExecuteNonQuery();
        }

        using var blockerTransaction = blocker.BeginTransaction();

        using (var write = blocker.CreateCommand())
        {
            write.CommandText = "UPDATE index_meta SET value = 'busy' WHERE key = 'fingerprint';";
            write.ExecuteNonQuery();
        }

        // 已經初始化過的實例：開連線只套用連線層級 PRAGMA，不會自己等逾時。
        using var reader = database.Open();

        var started = DateTime.UtcNow;

        Assert.ThrowsAny<SqliteException>(() =>
        {
            using var command = reader.CreateCommand();
            // 必須是**寫入**語句：DELETE journal 模式下寫者只持有 RESERVED，
            // 讀取仍可進行（SELECT 不會被擋），所以只有寫入才會吃到 SQLITE_BUSY。
            command.CommandText = "UPDATE index_meta SET value = 'x' WHERE key = 'fingerprint';";
            command.ExecuteNonQuery();
        });

        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed >= TimeSpan.FromSeconds(4), $"應該真的等過逾時，實際 {elapsed.TotalSeconds:0.0}s");
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"逾時應為 5 秒而非預設的 30 秒，實際 {elapsed.TotalSeconds:0.0}s");

        blockerTransaction.Rollback();
    }

    private static int ReadUserVersion(SqliteConnection connection)    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM pragma_table_info($table) WHERE name = $column);";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
    }
}
