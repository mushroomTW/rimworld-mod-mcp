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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "DI instance service method")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeSmell", "S2325:Methods that don't access instance data should be 'static'", Justification = "DI instance service method")]
    public IReadOnlyList<SourceHit> Search(
        SqliteConnection connection,
        string pattern,
        string? filePattern,
        int limit,
        string? assemblyLike = null)
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
            throw new ArgumentException($"Invalid search pattern: {e.Message}", nameof(pattern));
        }

        var cap = Math.Clamp(limit, 1, 800);
        var results = new List<SourceHit>(Math.Min(cap, 128));

        foreach (var (assembly, path, text) in Candidates(connection, pattern, filePattern, cap, assemblyLike))
        {
            var line = 0;

            foreach (var content in text.Split('\n'))
            {
                line++;

                if (!regex.IsMatch(content))
                {
                    continue;
                }

                // 縮排對呼叫端沒有意義，前導 tab 卻每列都要算 token。
                var trimmed = content.Trim();
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
    /// <summary>候選檔案內容的總量上限。反編譯檔動輒數百 KB，不設上限的話
    /// 最壞情況會把數百 MB 的字串一次拉進記憶體。</summary>
    private const long MaxCandidateChars = 32L * 1024 * 1024;

    private static IEnumerable<(string Assembly, string Path, string Text)> Candidates(
        SqliteConnection connection,
        string pattern,
        string? filePattern,
        int limit,
        string? assemblyLike)
    {
        var match = BuildMatch(pattern);

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

        // Mod 搜尋必須把組件前綴推進 SQL：遊戲本體的命中數壓倒性多於任何
        // 單一 Mod，先截斷再過濾的話，典型情況下過濾後是空的。
        // 沒有組件樣式就是遊戲搜尋：Mod 的原始碼與遊戲本體共用同一張表，
        // 不排除的話多版本 Mod 的同一行會在遊戲搜尋裡重複出現好幾次。
        sql += string.IsNullOrEmpty(assemblyLike)
            ? " AND s.assembly NOT LIKE 'mod:%'"
            : " AND s.assembly LIKE $assembly ESCAPE '\\'";

        // FTS 路徑用 bm25 相關性排序，最相關的檔案先掃。
        if (match.Length > 0)
        {
            sql += " ORDER BY rank";
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

        if (!string.IsNullOrEmpty(assemblyLike))
        {
            command.Parameters.AddWithValue("$assembly", assemblyLike);
        }

        command.Parameters.AddWithValue("$limit", limit * 4);

        using var reader = command.ExecuteReader();
        var consumed = 0L;

        while (reader.Read())
        {
            var text = reader.GetString(2);
            consumed += text.Length;

            yield return (reader.GetString(0), reader.GetString(1), text);

            if (consumed > MaxCandidateChars)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// 從 regex 模式建出 FTS5 候選查詢。alternation 的每個分支抽關鍵字後以 OR 相連；
    /// 若用 AND 相連，只含其中一支的檔案會在候選階段就被濾掉，regex 根本沒機會跑
    /// ——這是靜默的 false negative。抽不到關鍵字才回空字串，呼叫端退回掃描。
    /// </summary>
    private static string BuildMatch(string pattern)
    {
        var branches = SplitAlternation(pattern);
        var clauses = new List<string>(branches.Count);

        foreach (var branch in branches)
        {
            var keywords = KeywordsForBranch(branch);

            if (keywords.Length == 0)
            {
                continue;
            }

            clauses.Add(FtsQuery.Match(string.Join(' ', keywords)));
        }

        // 有任一分支抽不出關鍵字時，若直接用 OR 會漏召回（該分支的候選全丟），
        // 寧可退回全掃，保證 regex 有機會跑到所有候選。
        if (clauses.Count != branches.Count)
        {
            return string.Empty;
        }

        return clauses.Count switch
        {
            0 => string.Empty,
            1 => clauses[0],
            _ => string.Join(" OR ", clauses.Select(c => $"( {c} )")),
        };
    }

    /// <summary>
    /// 按「不在任何群組內」的未跳脫 <c>|</c> 切分支。
    ///
    /// <para>
    /// 群組深度以 <c>(</c>/<c>)</c> 計算，跳過 <c>\(</c>、<c>\)</c> 與字元類 <c>[...]</c>。
    /// lookaround（<c>(?!</c>、<c>(?=</c>、<c>(?&lt;!</c>、<c>(?&lt;=</c>）內的 <c>|</c> 是條件
    /// 不是分支，不能切——切開的話 <c>Foo(Bar|Baz)?</c> 會變成 <c>Foo(Bar</c> / <c>Baz)?</c>，
    /// 第一支抽到 <c>"Foo" AND "Bar"</c>，只含 Foo 的檔案在候選階段就被濾掉（靜默漏召回）。
    /// </para>
    /// <para>
    /// 整個 pattern 就是一個群組（<c>(a|b)</c>、<c>(DeregisterZone|Delete)</c>）時，先把
    /// 外層括號剝掉再切——內層的 <c>|</c> 語意上就是頂層分支，兩邊都是完整候選，維持
    /// 「分組 alternation 以 OR 取聯集」的行為。<c>(?…</c> 開頭（lookaround、non-capturing）
    /// 不剝，那些群組的內容不是完整候選。
    /// </para>
    /// </summary>
    private static List<string> SplitAlternation(string pattern)
    {
        if (pattern.Length >= 3 && pattern[0] == '(' && pattern[1] != '?')
        {
            var close = MatchingParen(pattern);

            if (close == pattern.Length - 1)
            {
                return SplitAlternation(pattern[1..^1]);
            }
        }

        var branches = new List<string>();
        var current = new System.Text.StringBuilder(pattern.Length);
        var escaped = false;
        var inClass = false;
        var depth = 0;

        foreach (var c in pattern)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    current.Append(c);
                    escaped = true;
                    break;
                case '[':
                    current.Append(c);
                    inClass = true;
                    break;
                case ']':
                    current.Append(c);
                    inClass = false;
                    break;
                case '(' when !inClass:
                    current.Append(c);
                    depth++;
                    break;
                case ')' when !inClass:
                    current.Append(c);

                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                case '|' when !inClass && depth == 0:
                    branches.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        branches.Add(current.ToString());
        return branches;
    }

    /// <summary><paramref name="pattern"/>[0] 的 <c>(</c> 對應的閉括號位置；找不到回 -1。</summary>
    private static int MatchingParen(string pattern)
    {
        var depth = 0;
        var escaped = false;
        var inClass = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    escaped = true;
                    break;
                case '[':
                    inClass = true;
                    break;
                case ']':
                    inClass = false;
                    break;
                case '(' when !inClass:
                    depth++;
                    break;
                case ')' when !inClass:
                    depth--;

                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// 抽出單一分支裡<b>必定出現</b>的字面詞當全文關鍵字。候選階段只求召回不求精準，
    /// 所以寧可少抽也不能抽錯：抽到一個其實不必出現的詞，含有目標的檔案會在候選階段
    /// 就被濾掉，regex 根本沒機會跑，結果是靜默的零筆。因此：
    /// 跳脫序列（<c>\b</c>、<c>\s</c>、<c>\.</c>）整個當作分隔——直接拿掉反斜線會把
    /// <c>\bFoo\b</c> 抽成 <c>bFoo</c>；字元類 <c>[...]</c> 與可選片段
    /// （<c>?</c>、<c>*</c>、<c>{0,</c> 之前的字元或群組）不是必定出現，一律不抽。
    /// source_fts 用 trigram tokenizer，關鍵字是子字串也找得到。
    /// </summary>
    private static string[] KeywordsForBranch(string branch)
    {
        var cleaned = branch;

        foreach (var (pattern, replacement) in KeywordCleanups)
        {
            cleaned = Regex.Replace(cleaned, pattern, replacement, RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        // 連續三個以上的英數字元（trigram 的最短可查長度）。
        var matches = Regex.Matches(
            cleaned,
            @"[A-Za-z_][A-Za-z0-9_]{2,}",
            RegexOptions.None,
            TimeSpan.FromSeconds(1));

        return [.. matches
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)];
    }

    /// <summary>依序套用；跳脫序列必須最先處理，<c>\[</c> 才不會被當成字元類的開頭。</summary>
    private static readonly (string Pattern, string Replacement)[] KeywordCleanups =
    [
        // \b \s \. \x41 A 等跳脫序列：整個換成分隔，不能只拿掉反斜線。
        (@"\\(?:x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|.)", " "),
        // 字元類 [abc]：只需其中一個字元，裡面的字面不是必定出現。
        (@"\[[^\]]*\]", " "),
        // lookaround (?!Bar) (?=Bar) (?<!Bar) (?<=Bar)：內容是條件不是必定出現，
        // 抽成必要關鍵字會把符合目標的檔案濾掉（Foo(?!Bar) 把 Bar 當必現詞）。
        (@"\(\?[=!]<?(?:[^()]|\([^()]*\))*\)", " "),
        // 含 alternation 的群組 (Foo|Bar)：其中一支可以不出現，整個群組都不是必定出現。
        // 要在前面那些只處理單一「原子」的規則之後，否則 Foo(Bar|Baz) 會先被吃成半截。
        (@"\([^()]*\|[^()]*\)(?:\?|\*|\{0,?\d*\})?", " "),
        // 可選的群組 (Foo)? (Foo)* (Foo){0,2}：整個群組可以不出現。
        (@"\([^()]*\)(?:\?|\*|\{0,?\d*\})", " "),
        // 可選的單一字元 o? o* o{0,3}：那個字元可以不出現，前面的字面仍然必定出現。
        (@"\w(?:\?|\*|\{0,?\d*\})", " "),
        // 其餘 regex 元字元都當分隔。
        (@"[^\w\s]", " "),
    ];

    /// <summary>
    /// glob → LIKE。必須自己逐字元轉換：LIKE 的跳脫（<c>%</c>、<c>_</c>）與
    /// glob 的萬用字元（<c>*</c>、<c>?</c>）是兩套不相干的字彙，先跳脫再替換
    /// 會找不到目標序列，讓 file_pattern 靜默失效。
    /// </summary>
    private static string GlobToLike(string pattern)
    {
        var builder = new System.Text.StringBuilder(pattern.Length + 8);

        foreach (var c in pattern)
        {
            switch (c)
            {
                case '*':
                    builder.Append('%');
                    break;
                case '?':
                    builder.Append('_');
                    break;
                case '%':
                    builder.Append(@"\%");
                    break;
                case '_':
                    builder.Append(@"\_");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
