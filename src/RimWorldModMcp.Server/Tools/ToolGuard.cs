using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace RimWorldModMcp.Server.Tools;

/// <summary>
/// 把預期內的領域失敗轉成 <see cref="McpException"/>。
///
/// <para>
/// MCP SDK 只會把 <see cref="McpException"/> 的訊息傳給呼叫端，其他例外一律變成
/// 「An error occurred invoking ...」。工具層精心撰寫的錯誤訊息（「不在信任的
/// 工作區內」「需要 confirm=true」）正是給 LLM 呼叫端自我修正用的，
/// 不經過這裡轉換，最需要提示的對象反而什麼都看不到。
/// </para>
/// </summary>
internal static class ToolGuard
{
    public static T Run<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (SqliteException e) when (DatabaseHint(e) is { } hint)
        {
            throw new McpException(hint);
        }
        catch (Exception e) when (IsExpected(e))
        {
            throw new McpException(e.Message);
        }
    }

    public static async Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (SqliteException e) when (DatabaseHint(e) is { } hint)
        {
            throw new McpException(hint);
        }
        catch (Exception e) when (IsExpected(e))
        {
            throw new McpException(e.Message);
        }
    }

    /// <summary>
    /// 索引資料庫的狀態問題要告訴呼叫端該怎麼辦。損毀時每一個查詢工具都會失敗，
    /// 只回「An error occurred」的話，呼叫端不知道 rebuild_index 就能重設；
    /// 被鎖住則是背景索引正在寫，稍後重試即可。其餘 SQLite 錯誤維持原樣進 stderr。
    /// </summary>
    internal static string? DatabaseHint(SqliteException e) => e.SqliteErrorCode switch
    {
        // SQLITE_CORRUPT / SQLITE_NOTADB
        11 or 26 => $"The index database is corrupt ({e.Message}). Call rebuild_index to reset and rebuild it.",
        // SQLITE_BUSY / SQLITE_LOCKED
        5 or 6 => $"The index database is busy ({e.Message}); a background indexing job is writing. Retry shortly.",
        _ => null,
    };

    /// <summary>
    /// 預期內的失敗：參數錯誤、邊界違規、找不到目標、檔案系統問題。
    /// 非預期的例外（程式錯誤）維持原樣，讓它進 stderr 日誌。
    /// </summary>
    private static bool IsExpected(Exception e) => e is
        ArgumentException
        or InvalidOperationException
        or UnauthorizedAccessException
        or KeyNotFoundException
        or IOException
        or NotSupportedException;
}
