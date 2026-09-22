using RimWorldModMcp.Indexing.Query;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>反編譯檔的逐行分頁：下一頁從 EndLine + 1 接著讀就能拼出整份。</summary>
public sealed class SourceExcerptTests
{
    private const string Text = "line1\r\nline2\r\nline3\r\nline4";

    [Fact]
    public void WholeFileFitsWhenUnderTheLimit()
    {
        var excerpt = SourceExcerpt.Take(Text, 1, 1024);

        Assert.Equal("line1\nline2\nline3\nline4", excerpt.Text);
        Assert.Equal((1, 4, 4, false), (excerpt.StartLine, excerpt.EndLine, excerpt.TotalLines, excerpt.Truncated));
    }

    [Fact]
    public void PagesStopAtWholeLinesAndChainByEndLine()
    {
        // 每行 5 bytes 加換行；12 bytes 剛好放兩行。
        var first = SourceExcerpt.Take(Text, 1, 12);
        var second = SourceExcerpt.Take(Text, first.EndLine + 1, 12);

        Assert.Equal("line1\nline2", first.Text);
        Assert.True(first.Truncated);
        Assert.Equal("line3\nline4", second.Text);
        Assert.Equal(3, second.StartLine);
        Assert.False(second.Truncated);
    }

    [Fact]
    public void OversizedSingleLineIsCutInsteadOfReturningNothing()
    {
        var excerpt = SourceExcerpt.Take("abcdefghij", 1, 4);

        Assert.Equal("abcd", excerpt.Text);
        Assert.True(excerpt.Truncated);
    }

    /// <summary>
    /// 超過檔尾必須回**空頁**，不能回最後一行。舊行為把 StartLine 夾到最後一行，
    /// 於是呼叫端用 end_line + 1 翻頁時會永遠拿到同一行，而且那頁是非空的
    ///（看起來像還有資料），分不出「還有內容」與「已經到底」。
    /// </summary>
    [Fact]
    public void StartLineBeyondTheEndReturnsAnEmptyPage()
    {
        var excerpt = SourceExcerpt.Take(Text, 99, 1024);

        Assert.Equal(string.Empty, excerpt.Text);
        Assert.Equal(4, excerpt.TotalLines);

        // 「第一個不存在的行號」是穩定的檔尾標記：重複請求得到同一個答案，
        // 而不是在同一個非空頁之間打轉。
        Assert.Equal(5, excerpt.StartLine);
        Assert.Equal(4, excerpt.EndLine);
        Assert.False(excerpt.Truncated);
    }

    /// <summary>
    /// F13：前導空行不可被吞掉，否則文字與行號不一致
    ///（Text 缺兩行，StartLine/EndLine 卻照原始行數推進）。
    /// 舊實作以 buffer.Length 判斷分隔符，空字串 Length 仍為 0 而不加換行。
    /// </summary>
    [Fact]
    public void LeadingBlankLinesArePreserved()
    {
        var excerpt = SourceExcerpt.Take("\n\nclass Demo {}", 1, 1024);

        Assert.Equal("\n\nclass Demo {}", excerpt.Text);
        Assert.Equal((1, 3, 3, false), (excerpt.StartLine, excerpt.EndLine, excerpt.TotalLines, excerpt.Truncated));
    }

    /// <summary>照文件用 end_line + 1 翻頁必須能前進，且最後會停在空頁。</summary>
    [Fact]
    public void PagingByEndLineReachesAnEmptyPageAndStops()
    {
        var page = SourceExcerpt.Take(Text, 1, 12);
        var pages = new List<string>();

        // 上限只是防禦性的：修正前這個迴圈不會結束，因為每一頁都是非空的。
        for (var guard = 0; guard < 10 && page.Text.Length > 0; guard++)
        {
            pages.Add(page.Text);
            page = SourceExcerpt.Take(Text, page.EndLine + 1, 12);
        }

        Assert.Equal(["line1\nline2", "line3\nline4"], pages);
        Assert.Equal(string.Empty, page.Text);
    }
}
