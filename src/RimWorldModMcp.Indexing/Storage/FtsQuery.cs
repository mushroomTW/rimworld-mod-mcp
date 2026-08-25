using System.Text;

namespace RimWorldModMcp.Indexing.Storage;

/// <summary>把使用者輸入轉成安全的 SQLite 查詢字串。</summary>
public static class FtsQuery
{
    /// <summary>
    /// 把使用者輸入轉成純字面的 FTS5 查詢。
    ///
    /// <para>
    /// FTS5 會把 <c>" * ^ : - NEAR</c> 等字元當成運算子，直接轉送使用者輸入會拋出語法錯誤，
    /// 在工具層面變成看不懂的失敗。作法是把每個詞包成帶引號的字面詞（詞尾的 <c>*</c>
    /// 保留為前綴搜尋），隱含的 AND 行為不變，但運算子字元失去語意。
    /// </para>
    /// <para>
    /// 真正需要跳脫的只有雙引號（加倍）；其餘運算子字元是靠「被包進 quoted phrase」
    /// 而失效，再由 tokenizer 當成非 token 字元丟棄。
    /// </para>
    /// <para>
    /// 回傳空字串代表「沒有可用的搜尋詞」（例如輸入只有 <c>*</c>），
    /// 呼叫端應該整個跳過 FTS 條件而不是傳一個空查詢進去。
    /// </para>
    /// </summary>
    public static string Match(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var parts = new List<string>();

        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var isPrefix = token.EndsWith('*');
            var body = isPrefix ? token[..^1] : token;

            body = body.Replace("\"", "\"\"", StringComparison.Ordinal);

            if (body.Length == 0)
            {
                continue;
            }

            parts.Add(isPrefix ? $"\"{body}\"*" : $"\"{body}\"");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// 跳脫 LIKE 的萬用字元。沒有這一步的話 <c>Pawn_X</c> 裡的底線會被當成
    /// 單字元萬用字元，把 <c>PawnAX</c> 之類的名稱也一起撈出來。
    /// 需搭配 SQL 裡的 <c>LIKE ? ESCAPE '\'</c>。
    /// </summary>
    public static string LikeLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        foreach (var character in value)
        {
            if (character is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
