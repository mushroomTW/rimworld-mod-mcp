using System.Text.Json;

namespace RimWorldModMcp.Core;

/// <summary>
/// 狀態檔的原子寫入。
///
/// <para>
/// Python 版是直接覆寫，寫到一半崩潰會留下半截 JSON，讀取端只能靜默退回空值——
/// 診斷或工作區登記就這樣無聲消失。這裡改成「先寫暫存檔再置換」，
/// 讀取端看到的永遠是完整的舊版或完整的新版。
/// </para>
/// </summary>
public static class AtomicJson
{
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 暫存檔放在同一個目錄，確保置換發生在同一個磁碟區上（跨磁碟區的 move 不是原子的）。
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("n")[..8];

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // 清理失敗不應遮蔽原本的錯誤。
            }

            throw;
        }
    }

    /// <summary>
    /// 安全讀取狀態檔；若檔案不存在、正在被寫入鎖定、或內容破損時回傳預設值（null）。
    /// </summary>
    public static T? Read<T>(string path, JsonSerializerOptions? options = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
