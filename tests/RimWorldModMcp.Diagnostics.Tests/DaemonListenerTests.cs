using System.Text.Json;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>NDJSON 接收路徑的驗證行為。</summary>
public sealed class DaemonListenerTests : IDisposable
{
    private const string Token = "the-real-token";

    private readonly string _root;
    private readonly StoreDirectories _store;
    private readonly DiagnosticStore _diagnostics;
    private readonly DaemonListener _listener;

    public DaemonListenerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-daemon-" + Guid.NewGuid().ToString("n")[..12]);
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _diagnostics = new DiagnosticStore(_store);

        var sessions = new TestSessionStore(_store);
        _listener = new DaemonListener(_store, RimWorldLocator.DefaultBridgePort, _diagnostics, sessions, new DaemonRecordStore(_store));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Line(string type, string token, string text)
        => DiagnosticPayloadJson(type, token, "first", text);

    private static string DiagnosticPayloadJson(string type, string token, string firstLine, string text)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = type,
            ["token"] = token,
            ["first_line"] = firstLine,
            ["text"] = text,
        });

    [Fact]
    public void ValidLineIsAccepted()
    {
        _listener.Accept(Line("error", Token, "NullReferenceException"), Token);

        var records = _diagnostics.Read();
        Assert.Single(records);
        Assert.Equal("error", records[0].Type);
        Assert.Equal("bridge", records[0].Source);
    }

    /// <summary>
    /// token 不符的訊息一律丟棄。這是唯一的驗證機制——
    /// 沒有它，本機任何程序都能往這個埠灌假診斷。
    /// </summary>
    [Fact]
    public void WrongTokenIsRejected()
    {
        _listener.Accept(Line("error", "forged-token", "boom"), Token);

        Assert.Empty(_diagnostics.Read());
    }

    /// <summary>沒有進行中的場次（沒有 token）時一律拒收。</summary>
    [Fact]
    public void MissingExpectedTokenRejectsEverything()
    {
        _listener.Accept(Line("error", Token, "boom"), null);
        _listener.Accept(Line("error", Token, "boom"), string.Empty);

        Assert.Empty(_diagnostics.Read());
    }

    [Fact]
    public void UnknownTypeIsIgnored()
    {
        _listener.Accept(Line("shutdown_computer", Token, "nope"), Token);

        Assert.Empty(_diagnostics.Read());
    }

    /// <summary>單一壞行不該拖垮整條連線，也不該讓 daemon 掛掉。</summary>
    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("")]
    [InlineData("{}")]
    public void MalformedLinesAreIgnoredWithoutThrowing(string line)
    {
        _listener.Accept(line, Token);

        Assert.Empty(_diagnostics.Read());
    }

    /// <summary>
    /// 收下的診斷絕對不能含有 token。
    ///
    /// <para>
    /// Python 版把整個訊息（含 token）原封不動存進 diagnostics.json，
    /// 並經 MCP 工具回傳給客戶端——Bridge token 就這樣外洩到磁碟與對話裡。
    /// </para>
    /// </summary>
    [Fact]
    public void TokenNeverReachesStorage()
    {
        _listener.Accept(Line("error", Token, "something failed"), Token);

        var raw = File.ReadAllText(_store.DiagnosticsFile);

        Assert.DoesNotContain(Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepeatedDiagnosticsAreDeduplicatedAndCounted()
    {
        for (var i = 0; i < 5; i++)
        {
            _listener.Accept(Line("error", Token, "same failure\n  at Verse.Thing.Tick()"), Token);
        }

        var records = _diagnostics.Read();

        Assert.Single(records);
        Assert.Equal(5, records[0].Count);
    }

    /// <summary>
    /// 同一個例外在不同前後文出現時，靠堆疊行辨識成同一筆。
    /// 這是去重能真的發揮作用的關鍵——每 tick 拋一次的例外前文都不一樣。
    /// </summary>
    [Fact]
    public void SameStackIsTreatedAsOneDiagnostic()
    {
        _listener.Accept(Line("error", Token, "tick 100 failed\n  at Verse.Thing.Tick()"), Token);
        _listener.Accept(Line("error", Token, "tick 250 failed\n  at Verse.Thing.Tick()"), Token);

        var records = _diagnostics.Read();

        Assert.Single(records);
        Assert.Equal(2, records[0].Count);
    }

    [Fact]
    public void DifferentTypesAreSeparateDiagnostics()
    {
        _listener.Accept(Line("error", Token, "same text"), Token);
        _listener.Accept(Line("warning", Token, "same text"), Token);

        Assert.Equal(2, _diagnostics.Read().Count);
    }

    [Fact]
    public void StorageIsCappedAtTwoHundred()
    {
        for (var i = 0; i < 250; i++)
        {
            _listener.Accept(Line("error", Token, $"failure number {i}"), Token);
        }

        Assert.Equal(200, _diagnostics.Read().Count);
    }

    [Fact]
    public void FindLocatesADiagnosticByHash()
    {
        _listener.Accept(Line("error", Token, "findable"), Token);

        var hash = _diagnostics.Read()[0].Hash;

        Assert.NotNull(_diagnostics.Find(hash));
        Assert.Null(_diagnostics.Find("0000000000000000"));
    }

    [Fact]
    public void ControlCharactersSurviveTheRoundTrip()
    {
        var text = "tab\there\nandcontrol";
        _listener.Accept(Line("error", Token, text), Token);

        Assert.Equal(text, _diagnostics.Read()[0].Text);
    }
}
