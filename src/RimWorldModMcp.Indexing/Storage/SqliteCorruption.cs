using Microsoft.Data.Sqlite;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>
/// SQLite 損毀錯誤的共用判定。三處（ToolGuard 的提示、Mod 背景索引的錯誤訊息、
/// rebuild_index 的重試）原本各自寫 e.SqliteErrorCode is 11 or 26，
/// 抽到這裡避免漏改其中一處——那是「損毀」的同一件事，不該有三份副本。
/// </summary>
public static class SqliteCorruption
{
    /// <summary>
    /// <see cref="SqliteException.SqliteErrorCode"/> 是否代表索引資料庫損毀。
    /// 11 = SQLITE_CORRUPT（檔案內容已損毀），26 = SQLITE_NOTADB（檔頭不是資料庫）。
    /// </summary>
    public static bool IsCorrupt(SqliteException e) => e.SqliteErrorCode is 11 or 26;
}
