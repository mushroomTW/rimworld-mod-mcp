using RimWorldModMcp.Indexing.Query;

namespace RimWorldModMcp.Indexing.Tests;

/// <summary>反編譯檔的逐行分頁：下一頁從 <c>EndLine + 1</c> 接著讀就能拼出整份。</summary>
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

    [Fact]
    public void StartLineBeyondTheEndIsClampedToTheLastLine()
    {
        var excerpt = SourceExcerpt.Take(Text, 99, 1024);

        Assert.Equal("line4", excerpt.Text);
        Assert.Equal(4, excerpt.StartLine);
    }
}
