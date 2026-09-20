using Microsoft.Data.Sqlite;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>
/// SQLite 錯誤碼的共用分類。
///
/// <para>
/// 判定原本散在四處各自寫（ToolGuard 的提示、Mod 背景索引的錯誤訊息、
/// rebuild_index 的重試、以及「忙碌」的判定），抽到這裡避免漏改其中一處
/// ——那是同一件事，不該有多份副本。
/// </para>
/// </summary>
public static class SqliteCorruption
{
    /// <summary>
    /// <see cref="SqliteException.SqliteErrorCode"/> 是否代表索引資料庫損毀。
    /// 11 = SQLITE_CORRUPT（檔案內容已損毀），26 = SQLITE_NOTADB（檔頭不是資料庫）。
    /// </summary>
    public static bool IsCorrupt(SqliteException e) => e.SqliteErrorCode is 11 or 26;

    /// <summary>
    /// 是否為**暫時性**的寫鎖衝突。5 = SQLITE_BUSY（有另一個寫者），
    /// 6 = SQLITE_LOCKED（同一連線／程序內的鎖衝突）。
    ///
    /// <para>
    /// 這一類失敗的正確反應是「稍後重試」，不是「回報損毀」也不是「記成永久失敗」。
    /// 對外訊息見 <c>ToolGuard.DatabaseHint</c>；Mod 背景索引據此決定不要寫進
    /// <c>_indexErrors</c>（寫進去就不會再自動重試了）。
    /// </para>
    /// </summary>
    public static bool IsTransientBusy(SqliteException e) => e.SqliteErrorCode is 5 or 6;
}
