using System.Text.Json;
using RimWorldModMcp.Bridge;

namespace RimWorldModMcp.PlatformContracts.Tests;

/// <summary>
/// Bridge 送出的 NDJSON 格式契約。
///
/// <para>
/// Bridge 跑在 Unity Mono 的 net472 上，不能相依 <c>System.Text.Json</c>，
/// 所以 JSON 是手寫逐字元組出來的。這個測試專案直接以原始碼連結
/// 引入同一份 <c>DiagnosticPayload.cs</c>，在行程內驗證那段邏輯。
/// </para>
/// <para>
/// Python 時代這件事得靠一個叫 EscapeProbe 的獨立主控台程式 + base64-over-stdin
/// 協定才能測——那整套已經隨著兩端同語言而移除。
/// </para>
/// </summary>
public sealed class DiagnosticPayloadContracts
{
    /// <summary>全部 C0 控制字元。RimWorld 的 log 常含 tab 與其他控制字元。</summary>
    private static readonly string AllC0Controls = new([.. Enumerable.Range(0, 32).Select(i => (char)i)]);

    public static TheoryData<string> NastyInputs() =>
    [
        string.Empty,
        "plain text",
        AllC0Controls,
        "",
        "含中文與 emoji 🎮 的訊息",
        "tab\there\tand\nnewline",
        "quote\" and backslash\\",
        "trailing backslash\\",
        // JSON 注入：log 內容不能偽造出額外的欄位。
        "\"}{\"type\":\"injected\",\"token\":\"stolen\"",
    ];

    [Theory]
    [MemberData(nameof(NastyInputs))]
    public void OutputIsValidJsonAndRoundTripsLosslessly(string text)
    {
        var line = DiagnosticPayload.BuildLine("error", "test-token", "probe", text);

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal("test-token", root.GetProperty("token").GetString());
        Assert.Equal("probe", root.GetProperty("first_line").GetString());

        // 最重要的一條：內容必須完全無損。
        Assert.Equal(text, root.GetProperty("text").GetString());
    }

    [Fact]
    public void TextCannotBreakOutOfTheJsonEnvelope()
    {
        var attack = "\"}{\"type\":\"injected\",\"token\":\"stolen\"";
        var line = DiagnosticPayload.BuildLine("warning", "real-token", "probe", attack);

        using var document = JsonDocument.Parse(line);

        Assert.Equal("warning", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("real-token", document.RootElement.GetProperty("token").GetString());

        // 注入的字串只會有一組真正的 "type" 鍵。
        Assert.Equal(1, CountOccurrences(line, "\"type\":\"warning\""));
        Assert.Equal(0, CountOccurrences(line, "\"type\":\"injected\""));
    }

    /// <summary>欄位順序是協定的一部分，不可以隨手調換。</summary>
    [Fact]
    public void FieldOrderIsStable()
    {
        var line = DiagnosticPayload.BuildLine("diagnostic", "tok", "first", "body");

        Assert.Equal("""{"type":"diagnostic","token":"tok","first_line":"first","text":"body"}""", line);
    }

    /// <summary>
    /// 負向對照組：證明前面的正向測試不是碰巧通過的。
    /// 裸的控制字元在 JSON 裡是非法的，沒有轉義就一定解析失敗。
    /// </summary>
    [Fact]
    public void UnescapedControlCharacterWouldBeRejected()
    {
        // 用 ThrowsAny：實際拋的是 JsonReaderException，而 Assert.Throws 要求型別完全相符。
        var handWritten = "{\"type\":\"error\",\"text\":\"has\ttab\"}";
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(handWritten));

        // 同一段文字經過 Bridge 的轉義之後就必須是合法 JSON。
        var escaped = DiagnosticPayload.BuildLine("error", "t", "f", "has\ttab");
        using var document = JsonDocument.Parse(escaped);
        Assert.Equal("has\ttab", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public void ControlCharactersUseLowercaseHexEscapes()
    {
        var line = DiagnosticPayload.BuildLine("error", "t", "f", "");

        Assert.Contains("\\u0001", line, StringComparison.Ordinal);
        Assert.Contains("\\u001f", line, StringComparison.Ordinal);
        Assert.Contains("\\u007f", line, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
