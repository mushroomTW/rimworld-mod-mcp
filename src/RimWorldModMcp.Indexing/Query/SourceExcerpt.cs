using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Indexing.Query;

/// <summary>反編譯檔的一段節錄。行號從 1 起算，<see cref="EndLine"/> 是節錄裡最後一個完整行。</summary>
public sealed record SourceExcerpt(string Text, int StartLine, int EndLine, int TotalLines, bool Truncated)
{
    /// <summary>
    /// 從 <paramref name="startLine"/> 起取最多 <paramref name="maxBytes"/> 個 UTF-8 位元組的整行。
    ///
    /// <para>
    /// 以行而不是位元組做分頁：搜尋結果給的就是行號，下一頁直接從 EndLine + 1
    /// 接著讀，呼叫端不必自己換算位元組偏移。只有單獨一行就超過上限時才會切在行中間。
    /// </para>
    /// </summary>
    public static SourceExcerpt Take(string text, int startLine, int maxBytes)
    {
        var lines = text.Split('\n');
        var total = lines.Length;
        var first = Math.Clamp(startLine, 1, Math.Max(total, 1));

        var buffer = new System.Text.StringBuilder();
        var bytes = 0;
        var last = first - 1;

        for (var i = first - 1; i < total; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var cost = System.Text.Encoding.UTF8.GetByteCount(line) + (buffer.Length > 0 ? 1 : 0);

            if (bytes + cost > maxBytes)
            {
                break;
            }

            if (buffer.Length > 0)
            {
                buffer.Append('\n');
            }

            buffer.Append(line);
            bytes += cost;
            last = i + 1;
        }

        // 第一行就塞不下：退回位元組截斷，至少讓呼叫端看得到開頭。
        if (last < first && first <= total)
        {
            var partial = Utf8Text.Truncate(lines[first - 1].TrimEnd('\r'), maxBytes, out _);
            return new SourceExcerpt(partial, first, first, total, Truncated: true);
        }

        return new SourceExcerpt(buffer.ToString(), first, last, total, Truncated: last < total);
    }
}
