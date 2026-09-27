using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Query;

/// <summary>原始碼搜尋的一筆命中。</summary>
public sealed record SourceHit(string Assembly, string File, int Line, string Text);

/// <summary>
/// 一次搜尋的結果。
/// </summary>
/// <param name="Hits">命中的行，最多 <c>limit</c> 筆。</param>
/// <param name="BudgetExceeded">
/// true 代表時間預算用完時還有候選檔案沒掃完——結果**不完整**。
/// 這必須回報給呼叫端：把「掃到一半」誤當成「掃完了」是靜默的 false negative。
/// 候選數上限與候選字元預算截斷同樣視為不完整（見 <see cref="IncompleteReason"/>）。
/// </param>
/// <param name="IncompleteReason">
/// 結果不完整的原因，完整時為 null。呼叫端據此繼續搜尋：
/// time_budget（時間預算用完，還有候選檔沒掃）→ 縮小範圍（file_pattern、
/// 更具體的 pattern、較小的 limit）後重查；candidate_bytes（候選字元預算用完）
/// → 同上，且優先以 file_pattern 切分 corpus 再分次查詢。
/// </param>
public sealed record SourceSearchResult(IReadOnlyList<SourceHit> Hits, bool BudgetExceeded, string? IncompleteReason = null);

/// <summary>
/// 對反編譯後的原始碼做搜尋。
///
/// <para>
/// 先用 FTS5 把候選檔案縮到一小撮，再對那些檔案逐行套用 regex。
/// Python 版是每次呼叫都完整掃過整個反編譯樹、零索引零快取——
/// Core 加 DLC 是數萬個檔案，每次查詢都要全部讀進記憶體。
/// </para>
/// </summary>
public static class SourceQueryService
{
    /// <summary>單行回傳的字元上限，避免壓縮過的長行灌爆輸出。</summary>
    private const int MaxLineLength = 1000;

    /// <summary>結果不完整的原因代碼（見 <see cref="SourceSearchResult"/>）。</summary>
    public const string IncompleteTimeBudget = "time_budget";

    /// <summary>結果不完整的原因代碼（見 <see cref="SourceSearchResult"/>）。</summary>
    public const string IncompleteCandidateBytes = "candidate_bytes";

    /// <summary>
    /// 單行比對的逾時。這是災難性回溯的偵測點。
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 整次搜尋的時間預算。
    ///
    /// <para>
    /// 單檔 5 秒的逾時 × 最多 <c>limit×4</c>（上限 3200）個候選檔，最壞可以累積好幾小時，
    /// 遠超過 MCP client 常見的 60 秒工具逾時——那時 client 已經報錯，server 還在跑，
    /// agent 看到的是「工具壞掉」而不是「結果不完整」。
    /// 用完預算就帶著目前為止的結果回來並標記 <see cref="SourceSearchResult.BudgetExceeded"/>。
    /// </para>
    /// </summary>
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(20);

    private const string MatchParam = "$match";
    private const string PathParam = "$path";
    private const string AssemblyParam = "$assembly";
    private const string LimitParam = "$limit";
    private const string OffsetParam = "$offset";

    private const string FtsDefaultSql = """
        SELECT s.assembly, s.path, s.text
        FROM source_file s
        JOIN source_fts f ON f.rowid = s.id
        WHERE source_fts MATCH $match
          AND s.assembly NOT LIKE 'mod:%'
        ORDER BY rank
        LIMIT $limit OFFSET $offset
        """;

    private const string FtsCustomAssemblySql = """
        SELECT s.assembly, s.path, s.text
        FROM source_file s
        JOIN source_fts f ON f.rowid = s.id
        WHERE source_fts MATCH $match
          AND s.assembly LIKE $assembly ESCAPE '\'
        ORDER BY rank
        LIMIT $limit OFFSET $offset
        """;

    private const string FtsPathDefaultSql = """
        SELECT s.assembly, s.path, s.text
        FROM source_file s
        JOIN source_fts f ON f.rowid = s.id
        WHERE source_fts MATCH $match
          AND s.path LIKE $path ESCAPE '\'
          AND s.assembly NOT LIKE 'mod:%'
        ORDER BY rank
        LIMIT $limit OFFSET $offset
        """;

    private const string FtsPathCustomAssemblySql = """
        SELECT s.assembly, s.path, s.text
        FROM source_file s
        JOIN source_fts f ON f.rowid = s.id
        WHERE source_fts MATCH $match
          AND s.path LIKE $path ESCAPE '\'
          AND s.assembly LIKE $assembly ESCAPE '\'
        ORDER BY rank
        LIMIT $limit OFFSET $offset
        """;

    private const string ScanDefaultSql = """
        SELECT assembly, path, text
        FROM source_file s
        WHERE s.assembly NOT LIKE 'mod:%'
        LIMIT $limit OFFSET $offset
        """;

    private const string ScanCustomAssemblySql = """
        SELECT assembly, path, text
        FROM source_file s
        WHERE s.assembly LIKE $assembly ESCAPE '\'
        LIMIT $limit OFFSET $offset
        """;

    private const string ScanPathDefaultSql = """
        SELECT assembly, path, text
        FROM source_file s
        WHERE s.path LIKE $path ESCAPE '\'
          AND s.assembly NOT LIKE 'mod:%'
        LIMIT $limit OFFSET $offset
        """;

    private const string ScanPathCustomAssemblySql = """
        SELECT assembly, path, text
        FROM source_file s
        WHERE s.path LIKE $path ESCAPE '\'
          AND s.assembly LIKE $assembly ESCAPE '\'
        LIMIT $limit OFFSET $offset
        """;

    public static SourceSearchResult Search(
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
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"Invalid search pattern: {e.Message}", nameof(pattern));
        }

        var cap = Math.Clamp(limit, 1, 800);
        var results = new List<SourceHit>(Math.Min(cap, 128));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // F01：候選查詢分頁，直到命中上限或實際用完時間／容量預算。
        // 固定只看前 limit*4 個檔時，目標在截斷點之後即靜默漏報（零筆＋未超預算）。
        const int pageSize = 500;
        var baseQuery = BuildCandidateQuery(pattern, filePattern, assemblyLike);
        var consumed = 0L;
        var offset = 0;

        while (true)
        {
            var (page, hasMore) = QueryCandidatePage(connection, baseQuery, pageSize, offset);

            foreach (var (assembly, path, text) in page)
            {
                if (stopwatch.Elapsed > SearchBudget)
                {
                    return new SourceSearchResult(results, BudgetExceeded: true, IncompleteReason: IncompleteTimeBudget);
                }

                consumed += text.Length;

                if (consumed > MaxCandidateChars)
                {
                    return new SourceSearchResult(results, BudgetExceeded: true, IncompleteReason: IncompleteCandidateBytes);
                }

                if (ProcessCandidate(regex, assembly, path, text, results, cap))
                {
                    return new SourceSearchResult(results, BudgetExceeded: false);
                }
            }

            if (!hasMore)
            {
                return new SourceSearchResult(results, BudgetExceeded: false);
            }

            offset += pageSize;
        }
    }

    private static bool ProcessCandidate(
        Regex regex,
        string assembly,
        string path,
        string text,
        List<SourceHit> results,
        int cap)
    {
        var line = 0;

        foreach (var raw in text.Split('\n'))
        {
            line++;

            // Windows 上的反編譯輸出是 \r\n；殘留的 \r 會讓 $ 錨點永遠比對不到。
            var content = raw.TrimEnd('\r');

            bool matched;

            try
            {
                matched = regex.IsMatch(content);
            }
            catch (RegexMatchTimeoutException)
            {
                // 單行超過 MatchTimeout 代表模式有災難性回溯。訊息要說「怎麼改」，
                // 而不是只說「逾時」——呼叫端是 LLM，它會照著訊息調整模式。
                // （RegexMatchTimeoutException 繼承 TimeoutException，
                //   所以 ToolGuard 會把這個訊息原樣送達。）
                throw new TimeoutException(
                    $"The pattern took longer than {MatchTimeout.TotalSeconds:0}s on a single line of {path}. "
                    + "Simplify it — prefer literal words (e.g. CurTimeSpeed) over nested quantifiers, "
                    + "or narrow the search with file_pattern.");
            }

            if (!matched)
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
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 用 FTS5 先縮小要掃的檔案範圍。
    /// 搜尋模式含有可當關鍵字的字面詞時才用得上；否則退回掃全部（受檔名樣式限制）。
    /// </summary>
    /// <summary>候選檔案內容的總量上限。反編譯檔動輒數百 KB，不設上限的話
    /// 最壞情況會把數百 MB 的字串一次拉進記憶體。</summary>
    private const long MaxCandidateChars = 32L * 1024 * 1024;

    private enum CandidateQueryKind
    {
        FtsDefault,
        FtsCustomAssembly,
        FtsPathDefault,
        FtsPathCustomAssembly,
        ScanDefault,
        ScanCustomAssembly,
        ScanPathDefault,
        ScanPathCustomAssembly,
    }

    private sealed record CandidateQuery(
        CandidateQueryKind Kind,
        string Match,
        string? PathLike,
        string? AssemblyLike,
        bool HasMatch,
        bool HasPath,
        bool HasAssembly);

    private static CandidateQuery BuildCandidateQuery(string pattern, string? filePattern, string? assemblyLike)
    {
        var match = BuildMatch(pattern);
        var hasMatch = match.Length > 0;
        var hasPath = !string.IsNullOrEmpty(filePattern) && filePattern != "*";
        var hasAssembly = !string.IsNullOrEmpty(assemblyLike);

        var kind = (hasMatch, hasPath, hasAssembly) switch
        {
            (true, false, false) => CandidateQueryKind.FtsDefault,
            (true, false, true) => CandidateQueryKind.FtsCustomAssembly,
            (true, true, false) => CandidateQueryKind.FtsPathDefault,
            (true, true, true) => CandidateQueryKind.FtsPathCustomAssembly,
            (false, false, false) => CandidateQueryKind.ScanDefault,
            (false, false, true) => CandidateQueryKind.ScanCustomAssembly,
            (false, true, false) => CandidateQueryKind.ScanPathDefault,
            (false, true, true) => CandidateQueryKind.ScanPathCustomAssembly,
        };

        return new CandidateQuery(
            kind,
            match,
            hasPath ? GlobToLike(filePattern!) : null,
            hasAssembly ? assemblyLike : null,
            hasMatch,
            hasPath,
            hasAssembly);
    }

    /// <summary>
    /// 取一頁候選檔案。`hasMore` 為 true 代表後面還有未掃檔案。
    /// 多取一筆偵測，避免恰好掃完時誤報不完整。
    /// </summary>
    private static (List<(string Assembly, string Path, string Text)> Page, bool HasMore) QueryCandidatePage(
        SqliteConnection connection,
        CandidateQuery baseQuery,
        int pageSize,
        int offset)
    {
        using var command = connection.CreateCommand();
        command.CommandText = baseQuery.Kind switch
        {
            CandidateQueryKind.FtsDefault => FtsDefaultSql,
            CandidateQueryKind.FtsCustomAssembly => FtsCustomAssemblySql,
            CandidateQueryKind.FtsPathDefault => FtsPathDefaultSql,
            CandidateQueryKind.FtsPathCustomAssembly => FtsPathCustomAssemblySql,
            CandidateQueryKind.ScanDefault => ScanDefaultSql,
            CandidateQueryKind.ScanCustomAssembly => ScanCustomAssemblySql,
            CandidateQueryKind.ScanPathDefault => ScanPathDefaultSql,
            CandidateQueryKind.ScanPathCustomAssembly => ScanPathCustomAssemblySql,
            _ => throw new InvalidOperationException($"Unsupported query kind: {baseQuery.Kind}"),
        };

        if (baseQuery.HasMatch)
        {
            command.Parameters.AddWithValue(MatchParam, baseQuery.Match);
        }

        if (baseQuery.HasPath)
        {
            command.Parameters.AddWithValue(PathParam, baseQuery.PathLike!);
        }

        if (baseQuery.HasAssembly)
        {
            command.Parameters.AddWithValue(AssemblyParam, baseQuery.AssemblyLike!);
        }

        command.Parameters.AddWithValue(LimitParam, pageSize + 1);
        command.Parameters.AddWithValue(OffsetParam, offset);

        using var reader = command.ExecuteReader();
        var items = new List<(string Assembly, string Path, string Text)>(Math.Min(pageSize, 256));

        while (reader.Read())
        {
            if (items.Count >= pageSize)
            {
                return (items, true);
            }

            items.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return (items, false);
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
    /// 按「不在任何群組內」的未跳脫 | 切分支。
    ///
    /// <para>
    /// 群組深度以 (/) 計算，跳過 \(、\) 與字元類 [...]。
    /// lookaround（前瞻與後瞻群組，例如以 (?=、(?! 開頭者）內的 | 是條件
    /// 不是分支，不能切——切開的話 Foo(Bar|Baz)? 會變成 Foo(Bar / Baz)?，
    /// 第一支抽到 "Foo" AND "Bar"，只含 Foo 的檔案在候選階段就被濾掉（靜默漏召回）。
    /// </para>
    /// <para>
    /// 整個 pattern 就是一個群組（(a|b)、(DeregisterZone|Delete)）時，先把
    /// 外層括號剝掉再切——內層的 | 語意上就是頂層分支，兩邊都是完整候選，維持
    /// 「分組 alternation 以 OR 取聯集」的行為。(?… 開頭（lookaround、non-capturing）
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

    /// <summary><paramref name="pattern"/>[0] 的 ( 對應的閉括號位置；找不到回 -1。</summary>
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
    /// 抽出單一分支裡必定出現的字面詞當全文關鍵字。候選階段只求召回不求精準，
    /// 所以寧可少抽也不能抽錯：抽到一個其實不必出現的詞，含有目標的檔案會在候選階段
    /// 就被濾掉，regex 根本沒機會跑，結果是靜默的零筆。因此：
    /// 跳脫序列（\b、\s、\.）整個當作分隔——直接拿掉反斜線會把
    /// \bFoo\b 抽成 bFoo；字元類 [...] 與可選片段
    /// （?、*、{0, 之前的字元或群組）不是必定出現，一律不抽。
    /// source_fts 用 trigram tokenizer，關鍵字是子字串也找得到。
    /// </summary>
    private static string[] KeywordsForBranch(string branch)
    {
        var cleaned = Apply(branch, AtomCleanups);

        // 群組規則反覆套用到不再變化：每條規則只認不含巢狀括號的群組，
        // 巢狀時要先剝掉內層，外層的可選／alternation 群組才認得出來。
        // 只跑一輪的話 (Foo(Bar))?Baz 會把 Foo、Bar 當成必現詞。
        for (var pass = 0; pass < 32; pass++)
        {
            var before = cleaned;
            cleaned = Apply(cleaned, GroupCleanups);

            if (cleaned == before)
            {
                break;
            }
        }

        cleaned = Apply(cleaned, SeparatorCleanups);

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

    private static string Apply(string text, (string Pattern, string Replacement)[] rules)
    {
        foreach (var (pattern, replacement) in rules)
        {
            text = Regex.Replace(text, pattern, replacement, RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        return text;
    }

    /// <summary>只跑一次；跳脫序列必須最先處理，\[ 才不會被當成字元類的開頭。</summary>
    private static readonly (string Pattern, string Replacement)[] AtomCleanups =
    [
        // 反向參考 \k<name> \k'name' 與 Unicode 類別 \p{Lu} \P{IsGreek}：
        // 名稱不是來源文字，整段當分隔（只拿掉 \k、\p 會把名稱抽成必現詞）。
        (@"\\k(?:<[^>]*>|'[^']*')", " "),
        (@"\\[pP]\{[^}]*\}", " "),
        // \b \s \. \x41 A 等跳脫序列：整個換成分隔，不能只拿掉反斜線。
        (@"\\(?:x[0-9A-Fa-f]{2}|u[0-9A-Fa-f]{4}|.)", " "),
        // 字元類 [abc]：只需其中一個字元，裡面的字面不是必定出現。
        (@"\[[^\]]*\]", " "),
        // 註解群組 (?#...)：內容不是來源文字。
        (@"\(\?#[^)]*\)", " "),
    ];

    /// <summary>反覆套用到不再變化，每一輪都只處理最內層（不含括號）的群組。</summary>
    private static readonly (string Pattern, string Replacement)[] GroupCleanups =
    [
        // 具名群組 (?<name>...) (?'name'...) (?P<name>...)：群組名稱不是來源文字，
        // 只剝掉名稱前綴、保留群組內容（(?<capture>RenderZone) 必然出現 RenderZone）。
        (@"\(\?<'?[A-Za-z_][A-Za-z0-9_]*'?>", "("),
        (@"\(\?P<[A-Za-z_][A-Za-z0-9_]*>", "("),
        // lookaround (?=Bar) (?!Bar) (?<=Bar) (?<!Bar)：內容是條件不是必定出現，
        // 抽成必要關鍵字會把符合目標的檔案濾掉（Foo(?!Bar) 把 Bar 當必現詞）。
        (@"\(\?(?:[=!]|<[=!])(?:[^()]|\([^()]*\))*\)", " "),
        // 含 alternation 的群組 (Foo|Bar)：其中一支可以不出現，整個群組都不是必定出現。
        // 要在前面那些只處理單一「原子」的規則之後，否則 Foo(Bar|Baz) 會先被吃成半截。
        (@"\([^()]*\|[^()]*\)(?:\?|\*|\{0,?\d*\})?", " "),
        // 可選的群組 (Foo)? (Foo)* (Foo){0,2}：整個群組可以不出現。
        (@"\([^()]*\)(?:\?|\*|\{0,?\d*\})", " "),
        // 其餘不可省略、不含 alternation 的最內層群組 (Bar)、(?:Bar)：內容必定出現，
        // 拆掉括號讓外層群組變成「不含巢狀」，下一輪的可選／alternation 規則才認得出來。
        // lookaround 已在前面移除，這裡再排除一次以免把條件內容拆成必現詞。
        (@"\((?!\?(?:[=!]|<[=!]))(?:\?:)?([^()|]*)\)(?![?*]|\{0)", "$1"),
    ];

    /// <summary>群組處理完之後才套用：元字元一旦換成分隔，括號結構就不見了。</summary>
    private static readonly (string Pattern, string Replacement)[] SeparatorCleanups =
    [
        // 可選的單一字元 o? o* o{0,3}：那個字元可以不出現，前面的字面仍然必定出現。
        (@"\w(?:\?|\*|\{0,?\d*\})", " "),
        // 其餘 regex 元字元都當分隔。
        (@"[^\w\s]", " "),
    ];

    /// <summary>
    /// glob → LIKE。必須自己逐字元轉換：LIKE 的跳脫（%、_）與
    /// glob 的萬用字元（*、?）是兩套不相干的字彙，先跳脫再替換
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
