using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Query;

/// <summary>原始碼搜尋的一筆命中。</summary>
public sealed record SourceHit(string Assembly, string File, int Line, string Text);

/// <summary>
/// 對反編譯後的原始碼做搜尋。
///
/// <para>
/// 先用 FTS5 把候選檔案縮到一小撮，再對那些檔案逐行套用 regex。
/// Python 版是每次呼叫都完整掃過整個反編譯樹、零索引零快取——
/// Core 加 DLC 是數萬個檔案，每次查詢都要全部讀進記憶體。
/// </para>
/// </summary>
public sealed class SourceQueryService
{
    /// <summary>單行回傳的字元上限，避免壓縮過的長行灌爆輸出。</summary>
    private const int MaxLineLength = 1000;

    public IReadOnlyList<SourceHit> Search(
        SqliteConnection connection,
        string pattern,
        string? filePattern,
        int limit)
    {
        Regex regex;

        try
        {
            // 一律不分大小寫，與 Python 版一致。
            // 注意 .NET 的 regex 方言與 Python 的 re 不完全相容（\b、\w 的 Unicode 語意、
            // 具名群組語法都有差異），這一點需要在文件中說明。
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"無效的搜尋模式：{e.Message}", nameof(pattern));
        }

        var cap = Math.Clamp(limit, 1, 800);
        var results = new List<SourceHit>(Math.Min(cap, 128));

        foreach (var (assembly, path, text) in Candidates(connection, pattern, filePattern, cap))
        {
            var line = 0;

            foreach (var content in text.Split('\n'))
            {
                line++;

                if (!regex.IsMatch(content))
                {
                    continue;
                }

                var trimmed = content.TrimEnd('\r');
                results.Add(new SourceHit(
                    assembly,
                    path,
                    line,
                    trimmed.Length > MaxLineLength ? trimmed[..MaxLineLength] : trimmed));

                if (results.Count >= cap)
                {
                    return results;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 用 FTS5 先縮小要掃的檔案範圍。
    /// 搜尋模式含有可當關鍵字的字面詞時才用得上；否則退回掃全部（受檔名樣式限制）。
    /// </summary>
    private static IEnumerable<(string Assembly, string Path, string Text)> Candidates(
        SqliteConnection connection,
        string pattern,
        string? filePattern,
        int limit)
    {
        var keywords = LiteralKeywords(pattern);
        var match = keywords.Length > 0 ? FtsQuery.Match(string.Join(' ', keywords)) : string.Empty;

        var sql = match.Length > 0
            ? """
              SELECT s.assembly, s.path, s.text
              FROM source_file s
              JOIN source_fts f ON f.rowid = s.id
              WHERE source_fts MATCH $match
              """
            : "SELECT assembly, path, text FROM source_file s WHERE 1=1";

        if (!string.IsNullOrEmpty(filePattern) && filePattern != "*")
        {
            sql += " AND s.path LIKE $path ESCAPE '\\'";
        }

        // 候選檔案數放寬到命中上限的數倍：一個檔案裡可能有多行命中，
        // 但也可能一行都沒有（FTS 命中的關鍵字出現在別處）。
        sql += " LIMIT $limit";

        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (match.Length > 0)
        {
            command.Parameters.AddWithValue("$match", match);
        }

        if (!string.IsNullOrEmpty(filePattern) && filePattern != "*")
        {
            command.Parameters.AddWithValue("$path", GlobToLike(filePattern));
        }

        command.Parameters.AddWithValue("$limit", limit * 4);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            yield return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        }
    }

    /// <summary>
    /// 從 regex 模式抽出可當全文關鍵字的字面詞。
    /// 抽不到就回空陣列，呼叫端會退回掃描全部檔案。
    /// </summary>
    private static string[] LiteralKeywords(string pattern)
    {
        // 連續三個以上的英數字元，且前後沒有 regex 元字元干擾的片段。
        var matches = Regex.Matches(pattern, @"(?<![\\\[\](){}|*+?.])[A-Za-z_][A-Za-z0-9_]{2,}");

        return [.. matches
            .Select(m => m.Value)
            .Where(v => !ReservedWords.Contains(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)];
    }

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // regex 的字元類別縮寫展開後看起來像字面詞，但不是。
        "sSwWbBdDnrtfv",
    };

    private static string GlobToLike(string pattern)
        => FtsQuery.LikeLiteral(pattern).Replace(@"\*", "%", StringComparison.Ordinal).Replace(@"\?", "_", StringComparison.Ordinal);
}
