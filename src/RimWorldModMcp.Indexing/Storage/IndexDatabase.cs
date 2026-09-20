using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>索引資料庫的連線工廠。</summary>
public sealed class IndexDatabase(StoreDirectories store)
{
    private bool _initialised;
    private readonly Lock _gate = new();

    public string DatabasePath => store.IndexDatabaseFile;

    /// <summary>開啟一條已套用結構與 PRAGMA 的連線。</summary>
    public SqliteConnection Open()
    {
        EnsureInitialised();

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());

        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    /// <summary>把資料庫整個刪掉重建。結構版本不符或要求全量重建時使用。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles();
            _initialised = false;
        }
    }

    private void EnsureInitialised()
    {
        if (_initialised)
        {
            return;
        }

        lock (_gate)
        {
            if (_initialised)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
            {
                connection.Open();
                ApplyPragmas(connection);

                if (!IsUsable(connection))
                {
                    // 結構過期或檔案損毀都一樣處理：索引是衍生資料，直接丟掉重建
                    // 比寫 migration 或修復划算。損毀不在這裡攔的話，之後每一個
                    // 查詢工具都會各自失敗，而只有 rebuild_index 會重設。
                    connection.Close();
                    SqliteConnection.ClearAllPools();
                    DeleteDatabaseFiles();

                    using var fresh = new SqliteConnection($"Data Source={DatabasePath}");
                    fresh.Open();
                    ApplyPragmas(fresh);
                    IndexSchema.Apply(fresh);
                    WriteUserVersion(fresh, IndexSchema.Version);

                    _initialised = true;
                    return;
                }

                IndexSchema.Apply(connection);
                WriteUserVersion(connection, IndexSchema.Version);
            }

            _initialised = true;
        }
    }

    /// <summary>
    /// 資料庫目前是否健康（結構版本相符且 quick_check 通過）。
    /// 純偵測，不觸發任何重建或重設——rimworld_status 用它在「行程中途
    /// 損毀」時仍能回報 healthy=false，呼叫端才知道要 rebuild_index。
    /// 每次呼叫跑一次 quick_check，大資料庫可能耗時數百毫秒；status 不是
    /// 熱路徑，可接受。
    /// </summary>
    public bool Healthy()
    {
        try
        {
            using var connection = Open();
            return IsUsable(connection);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>
    /// 結構版本相符且 quick_check 通過才算可用。任何一步拋出 SQLite 例外
    /// （例如檔頭已經不是資料庫）也視為不可用。
    /// </summary>
    private static bool IsUsable(SqliteConnection connection)
    {
        try
        {
            var version = ReadUserVersion(connection);

            if (version != 0 && version != IndexSchema.Version)
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";
            return command.ExecuteScalar() as string == "ok";
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void DeleteDatabaseFiles()
    {
        foreach (var suffix in (ReadOnlySpan<string>)["", "-wal", "-shm"])
        {
            var path = DatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        // WAL 在網路磁碟與 OneDrive 這類同步資料夾上會失敗，
        // 而使用者的快取目錄有可能就落在那些地方，所以要能退回 DELETE 模式。
        if (!TryExecute(connection, "PRAGMA journal_mode=WAL;"))
        {
            TryExecute(connection, "PRAGMA journal_mode=DELETE;");
        }

        TryExecute(connection, "PRAGMA synchronous=NORMAL;");
        TryExecute(connection, "PRAGMA busy_timeout=5000;");
        TryExecute(connection, "PRAGMA foreign_keys=ON;");
    }

    private static bool TryExecute(SqliteConnection connection, string sql)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void WriteUserVersion(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        // PRAGMA 不接受參數繫結，這裡的值來自編譯期常數而非使用者輸入。
#pragma warning disable S2077 // PRAGMA 不支援參數繫結，version 來自內部編譯期常數
        command.CommandText = $"PRAGMA user_version={version};";
#pragma warning restore S2077
        command.ExecuteNonQuery();
    }
}
