using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Diagnostics;

namespace RimWorldModMcp.Diagnostics.Tests;

/// <summary>
/// 真 TCP 層的行為：長度上限與逐行解析。
/// Accept() 的單元測試繞過了網路層，OOM 類的缺陷正落在那個縫裡。
/// </summary>
public sealed class DaemonListenerTcpTests : IDisposable
{
    private const string Token = "tcp-test-token";

    private readonly string _root;
    private readonly StoreDirectories _store;
    private readonly DiagnosticStore _diagnostics;
    private readonly DaemonRecordStore _records;
    private readonly DaemonListener _listener;
    private readonly int _port;

    public DaemonListenerTcpTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rwmm-tcp-" + Guid.NewGuid().ToString("n")[..12]);
        _store = new StoreDirectories(Path.Combine(_root, "data"), Path.Combine(_root, "cache"));
        _diagnostics = new DiagnosticStore(_store);
        _records = new DaemonRecordStore(_store);

        _port = FreeLoopbackPort();

        Directory.CreateDirectory(Path.GetDirectoryName(_store.BridgeTokenFile)!);
        File.WriteAllText(_store.BridgeTokenFile, Token);

        _listener = new DaemonListener(
            _store,
            _port,
            _diagnostics,
            new TestSessionStore(_store),
            _records,
            new GameStateStore(_store));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string ValidLine(string text) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "error",
        ["token"] = Token,
        ["first_line"] = "first",
        ["text"] = text,
    });

    [Fact]
    public async Task ActiveConnectionOutlivesIdleTimeoutWhileLinesKeepArriving()
    {
        // 逾時是「閒置」逾時：只要持續有資料進來，連線活得比逾時長也不該被切。
        var idle = TimeSpan.FromMilliseconds(400);
        var listener = new DaemonListener(_store, _port, _diagnostics, new TestSessionStore(_store), _records, new GameStateStore(_store), idle);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = listener.RunAsync(cancellation.Token);

        while (_records.Read() is null)
        {
            Assert.False(running.IsCompleted, "daemon 不應在啟動階段就結束");
            await Task.Delay(50, cancellation.Token);
        }

        const int lines = 6;

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, _port, cancellation.Token);
            var stream = client.GetStream();

            // 每 200ms 送一行，總時長 1.2s，是閒置逾時的三倍。
            for (var i = 0; i < lines; i++)
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(ValidLine("line " + i) + "\n"), cancellation.Token);
                await Task.Delay(200, cancellation.Token);
            }

            // 最後一行送出後再等一下，確認 daemon 有機會處理完。
            while (_diagnostics.Read().Count < lines && !cancellation.IsCancellationRequested)
            {
                if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0)
                {
                    break; // 對端已關閉，等也沒用。
                }

                await Task.Delay(50, cancellation.Token);
            }
        }

        Assert.Equal(lines, _diagnostics.Read().Count);

        cancellation.Cancel();

        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // 預期的關閉路徑。
        }
    }

    [Fact]
    public async Task IdleConnectionIsClosedAfterTimeout()
    {
        var idle = TimeSpan.FromMilliseconds(300);
        var listener = new DaemonListener(_store, _port, _diagnostics, new TestSessionStore(_store), _records, new GameStateStore(_store), idle);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = listener.RunAsync(cancellation.Token);

        while (_records.Read() is null)
        {
            Assert.False(running.IsCompleted, "daemon 不應在啟動階段就結束");
            await Task.Delay(50, cancellation.Token);
        }

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, _port, cancellation.Token);

            // 什麼都不送：daemon 關閉連線時 ReadAsync 會回 0（EOF）。
            var buffer = new byte[1];
            var read = await client.GetStream().ReadAsync(buffer, cancellation.Token);

            Assert.Equal(0, read);
        }

        cancellation.Cancel();

        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // 預期的關閉路徑。
        }
    }

    [Fact]
    public async Task OversizedLinesAreDroppedWhileValidLinesAreAccepted()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var running = _listener.RunAsync(cancellation.Token);

        // 等 daemon 寫下自述檔（bind 成功的證明）。
        while (_records.Read() is null)
        {
            Assert.False(running.IsCompleted, "daemon 不應在啟動階段就結束");
            await Task.Delay(50, cancellation.Token);
        }

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, _port, cancellation.Token);

            var stream = client.GetStream();

            // 一條遠超過上限的行（不含換行）——沒有長度上限時它會被整條
            // 累積在記憶體裡；有上限時應被整行丟棄。
            var oversized = new byte[400 * 1024];
            Array.Fill(oversized, (byte)'a');
            await stream.WriteAsync(oversized, cancellation.Token);
            await stream.WriteAsync("\n"u8.ToArray(), cancellation.Token);

            // 接著同一條連線送一行合法診斷，驗證連線沒有被超長行毀掉。
            var valid = Encoding.UTF8.GetBytes(ValidLine("real failure") + "\n");
            await stream.WriteAsync(valid, cancellation.Token);
            await stream.FlushAsync(cancellation.Token);

            // 等待這一筆進入儲存。
            while (_diagnostics.Read().Count == 0)
            {
                await Task.Delay(50, cancellation.Token);
            }
        }

        var records = _diagnostics.Read();

        Assert.Single(records);
        Assert.Equal("error", records[0].Type);
        Assert.Equal("real failure", records[0].Text);

        cancellation.Cancel();

        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
