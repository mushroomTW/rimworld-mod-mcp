using Microsoft.Data.Sqlite;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>索引資料庫的連線工廠。</summary>
public sealed class IndexDatabase(StoreDirectories store)
{
    /// <summary>
    /// 等待寫鎖的上限（秒）。
    ///
    /// <para>
    /// 這個值必須經由連線的 <c>DefaultTimeout</c> 設定，**不能**只靠
    /// <c>PRAGMA busy_timeout</c>：Microsoft.Data.Sqlite 在每個命令執行前會用
    /// <c>SqliteCommand.CommandTimeout</c>（預設繼承自連線的 <c>DefaultTimeout</c>，
    /// 也就是 30 秒）呼叫 <c>sqlite3_busy_timeout</c>，把 PRAGMA 設的值覆寫掉。
    /// 只寫 PRAGMA 的話實際等待是 30 秒——工具層卻對外宣稱「稍後重試即可」，
    /// 而 30 秒已足以讓 MCP client 的單次工具呼叫逾時。
    /// </para>
    /// </summary>
    private const int BusyTimeoutSeconds = 5;

    private bool _initialised;
    private readonly Lock _gate = new();

    public string DatabasePath => store.IndexDatabaseFile;

    /// <summary>開啟一條已套用結構與 PRAGMA 的連線。</summary>
    public SqliteConnection Open()
    {
        EnsureInitialised();

        var connection = CreateConnection();

        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    /// <summary>
    /// 建立連線物件（尚未 Open）。
    /// 逾時走連線字串而不是 <c>PRAGMA busy_timeout</c>，理由見 <see cref="BusyTimeoutSeconds"/>。
    /// </summary>
    private SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = BusyTimeoutSeconds,
    }.ToString());

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

    /// <summary>
    /// 索引資料庫的健康狀態。**忙碌**與**損毀**必須分開。
    ///
    /// <para>
    /// 回同一個 false 會產生兩個具體的錯誤行為：（1）<c>rimworld_status</c> 在
    /// 重建進行中叫人「再呼叫一次 rebuild_index」；（2）更嚴重的是
    /// <see cref="EnsureInitialised"/> 把忙碌當成不可用，於是**刪掉另一個
    /// 正在寫的實例的資料庫**。
    /// </para>
    /// </summary>
    public enum Health
    {
        /// <summary>結構版本相符且 quick_check 通過。</summary>
        Ok,

        /// <summary>有另一個寫者持有寫鎖，狀態未知——稍後再問，不要動它。</summary>
        Busy,

        /// <summary>結構版本不符或檔案損毀；rebuild_index 會重設並重建。</summary>
        Unusable,
    }

    /// <summary>
    /// 純偵測資料庫狀態，不觸發任何重建或重設。
    /// <c>rimworld_status</c> 用它在「行程中途損毀」時仍能回報，呼叫端才知道要 rebuild_index。
    /// 每次呼叫跑一次 quick_check，大資料庫可能耗時數百毫秒；status 不是熱路徑，可接受。
    /// </summary>
    public Health Check()
    {
        try
        {
            using var connection = Open();

            return Classify(connection) switch
            {
                Usability.Ok => Health.Ok,
                Usability.Unusable => Health.Unusable,

                // 問不到（忙碌、IO、權限）：回報「稍後再問」而不是「壞掉」。
                // 後者會讓 rimworld_status 在重建進行中叫人再重建一次。
                _ => Health.Busy,
            };
        }
        catch (SqliteException e) when (SqliteCorruption.IsTransientBusy(e))
        {
            return Health.Busy;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // 開不起來也一樣：問不到 ≠ 壞掉。回報 Unusable 會叫人去 rebuild，
            // 而 rebuild 同樣開不起來——那只會讓呼叫端在原地打轉。
            return Health.Busy;
        }
    }

    /// <summary>
    /// 資料庫的可用性判定。
    ///
    /// <para>
    /// <see cref="Unknown"/> 與 <see cref="Unusable"/> 的區分是**破壞性**的：
    /// 只有 <see cref="Unusable"/> 會觸發刪檔重建。<c>Unusable</c> 必須來自
    /// **正面證據**（版本不符、quick_check 報錯、SQLITE_CORRUPT／NOTADB）；
    /// 「問不到」（忙碌、IO 錯誤、權限）一律是 <see cref="Unknown"/>，
    /// 不能據此刪掉使用者的索引——那是不可逆的，而問不到不代表壞掉。
    /// </para>
    /// </summary>
    private enum Usability
    {
        /// <summary>結構版本相符且 quick_check 通過。</summary>
        Ok,

        /// <summary>有正面證據指出結構過期或檔案損毀。</summary>
        Unusable,

        /// <summary>問不到（忙碌、IO、權限）。**不能**據此刪檔。</summary>
        Unknown,
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

            using (var connection = CreateConnection())
            {
                connection.Open();
                ApplyConnectionPragmas(connection);
                EnsureJournalMode(connection);

                switch (Classify(connection))
                {
                    case Usability.Ok:
                        IndexSchema.Apply(connection);
                        WriteUserVersion(connection, IndexSchema.Version);
                        _initialised = true;
                        return;

                    case Usability.Unknown:
                        // 問不到就**不要動它**。刪檔是不可逆的，而忙碌、暫時的 IO 錯誤、
                        // 權限問題都不是「壞掉」。查詢真的不行會以可讀的錯誤回報
                        //（ToolGuard 會說「資料庫忙碌，請稍後重試」），
                        // 真正的損毀則由 rebuild_index 的 Reset 路徑處理——那也是可恢復的。
                        //
                        // 注意：這裡刻意不標記 _initialised，下次 Open 會重試。
                        // 若在此置 true，首次開啟正好遇到另一個寫者且庫尚未初始化時，
                        // 該實例此後永遠跳過 schema 建立，查詢以 no such table 失敗到重啟。
                        return;

                    default:
                        // Unusable：有正面證據。索引是衍生資料，直接丟掉重建比寫
                        // migration 或修復划算。損毀不在這裡攔的話，之後每一個查詢工具
                        // 都會各自失敗，而只有 rebuild_index 會重設。
                        break;
                }

                connection.Close();
                SqliteConnection.ClearAllPools();
                DeleteDatabaseFiles();

                using var fresh = CreateConnection();
                fresh.Open();
                ApplyConnectionPragmas(fresh);
                EnsureJournalMode(fresh);
                IndexSchema.Apply(fresh);
                WriteUserVersion(fresh, IndexSchema.Version);

                _initialised = true;
                return;
            }
        }
    }

    /// <summary>
    /// 判定資料庫可用性。**只有回傳 <see cref="Usability.Unusable"/> 才會導致刪檔**，
    /// 所以每一條路徑都必須是「有證據」而不是「不確定」。
    /// </summary>
    private static Usability Classify(SqliteConnection connection)
    {
        try
        {
            var version = ReadUserVersion(connection);

            if (version != IndexSchema.Version)
            {
                // user_version 為 0 只代表「從沒寫過版本戳記」，不代表結構正確。
                // 只有**完全空白的庫**才放行——那會由後續的 IndexSchema.Apply 建起來。
                //
                // 有表卻沒有版本戳記的舊庫若放行，CREATE TABLE IF NOT EXISTS 不會修正
                // 既有的表，WriteUserVersion 卻會把它蓋成現行版本，之後才在執行期以
                // "no such column" 失敗——而那個錯誤既不指向損毀也不指向忙碌，
                // 呼叫端只會看到「An error occurred」。
                if (version != 0 || HasAnyTable(connection))
                {
                    return Usability.Unusable;
                }
            }

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check(1);";

            // quick_check 回 "ok"，或第一筆錯誤訊息。非 "ok" 是正面證據。
            return command.ExecuteScalar() as string == "ok" ? Usability.Ok : Usability.Unusable;
        }
        catch (SqliteException e) when (SqliteCorruption.IsCorrupt(e))
        {
            // SQLITE_CORRUPT／SQLITE_NOTADB：正面證據。
            return Usability.Unusable;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // 忙碌、IO 錯誤、權限不足……都是「問不到」而不是「壞掉」。
            return Usability.Unknown;
        }
    }

    /// <summary>資料庫裡是否已經有任何資料表（含 SQLite 自己的內部資料表）。</summary>
    private static bool HasAnyTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table');";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
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

    /// <summary>
    /// 連線層級的 PRAGMA。每次開連線都要套用。
    ///
    /// <para>
    /// 等待寫鎖的逾時是由 <see cref="SqliteConnection.DefaultTimeout"/> 決定的
    ///（見 <see cref="BusyTimeoutSeconds"/> 的說明）；這裡的 <c>busy_timeout</c>
    /// 只是讓連線狀態與意圖一致，真正生效的是前者。
    /// </para>
    /// </summary>
    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        TryExecute(connection, "PRAGMA busy_timeout=5000;");
        TryExecute(connection, "PRAGMA synchronous=NORMAL;");
        TryExecute(connection, "PRAGMA foreign_keys=ON;");
    }

    /// <summary>
    /// 確立 journal mode。
    ///
    /// <para>
    /// WAL 在網路磁碟與 OneDrive 這類同步資料夾上會失敗，而使用者的快取目錄有可能
    /// 就落在那些地方，所以要能退回 DELETE 模式。
    /// </para>
    /// <para>
    /// 這是**持久性**屬性（寫在檔案標頭），所以只在 <see cref="EnsureInitialised"/>
    /// 做一次，不放在每條連線的 PRAGMA 裡——每次開連線都嘗試切換 journal mode
    /// 除了白花時間，在 DELETE 模式下還會為了拿鎖而卡滿逾時。
    /// </para>
    /// </summary>
    private static void EnsureJournalMode(SqliteConnection connection)
    {
        if (!TryExecute(connection, "PRAGMA journal_mode=WAL;"))
        {
            TryExecute(connection, "PRAGMA journal_mode=DELETE;");
        }
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
