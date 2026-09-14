using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 接收遊戲內 Bridge 送來的診斷。
///
/// <para>
/// 協定是 NDJSON over TCP，只綁 127.0.0.1。每一行必須帶正確的 token，
/// 那個 token 是每次測試場次隨機產生、透過環境變數注入遊戲的。
/// </para>
/// </summary>
public sealed class DaemonListener(
    StoreDirectories store,
    int bridgePort,
    DiagnosticStore diagnostics,
    TestSessionStore sessions,
    DaemonRecordStore records,
    TimeSpan? readTimeout = null)
{
    /// <summary>只接受這些訊息類型，其餘一律丟棄。</summary>
    private static readonly HashSet<string> AcceptedTypes = new(StringComparer.Ordinal)
    {
        "error", "warning", "diagnostic", "loaded_mods", "performance",
    };

    /// <summary>
    /// 單行 payload 的位元組上限。沒有上限的話，任何本機程序（token 驗證
    /// 發生在解析之後）送一條不含換行的超長資料就能把 daemon 撐爆記憶體。
    /// </summary>
    private const int MaxLineBytes = 256 * 1024;

    /// <summary>同時處理的連線數上限；超過的連線排隊等待。</summary>
    private static readonly SemaphoreSlim ConnectionThrottle = new(32, 32);

    /// <summary>
    /// 單一連線的閒置逾時。開著不送資料的連線不能永久佔住資源。
    /// 可注入是為了讓測試在秒級內驗證逾時行為，正式環境用預設值。
    /// </summary>
    private readonly TimeSpan _readTimeout = readTimeout ?? TimeSpan.FromMinutes(2);

    /// <summary>
    /// 啟動並執行 daemon 直到取消。
    ///
    /// <para>
    /// <b>先 bind 再寫自述檔</b>：綁定成功才代表這個埠真的是我們的。
    /// 反過來寫的話，另一個程序佔著埠時我們仍會留下自述檔，
    /// 讓後續的判斷誤以為埠是自己人佔的。
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, bridgePort);

        listener.Start();

        try
        {
            records.Write(new DaemonRecord
            {
                Pid = Environment.ProcessId,
                Port = bridgePort,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                StartTimeUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            });

            var tailer = new PlayerLogTailer(sessions, diagnostics);
            var tailing = tailer.RunAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 每個連線各自處理，一個壞掉的連線不影響其他連線。
                _ = HandleAsync(client, cancellationToken);
            }

            await tailing;
        }
        finally
        {
            listener.Stop();
            records.ClearIfOwnedBy(Environment.ProcessId);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            // 節流：fire-and-forget 的連線處理若沒有上限，開一萬條連線
            // 就能耗盡 socket handle 與執行緒池。
            await ConnectionThrottle.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                // token 在每個連線建立時讀一次。Bridge 是單一長連線，但它跟遊戲
                // 行程同生共死，而場次一定伴隨新的遊戲行程，所以新場次的 token
                // 必然在新連線上生效。
                var expected = ReadToken();

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(_readTimeout);

                await foreach (var line in ReadLinesAsync(client.GetStream(), timeoutSource.Token).ConfigureAwait(false))
                {
                    Accept(line, expected);

                    // 逾時是「閒置」逾時：每收到一行就重新計時。不重設的話，
                    // Bridge 的長連線一到 2 分鐘就被切，切斷後它的第一次寫入
                    // 通常仍成功（資料進了送出緩衝區才收到 RST），那一筆就靜默遺失。
                    timeoutSource.CancelAfter(_readTimeout);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 連線中斷是常態（遊戲關閉、寫到一半退出），閒置逾時也走這裡。
            }
            catch
            {
                // fire-and-forget 的例外沒有人 await——任何非預期例外都不能
                // 往外拋，否則變成 unobserved task exception 被靜默丟棄。
            }
            finally
            {
                ConnectionThrottle.Release();
            }
        }
    }

    /// <summary>
    /// 逐行讀取，行長超過 <see cref="MaxLineBytes"/> 時丟棄該連線的剩餘資料。
    /// 不能用 StreamReader.ReadLineAsync——它沒有長度上限，會把整條超長行
    /// 累積在記憶體裡。
    /// </summary>
    private static async IAsyncEnumerable<string> ReadLinesAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var line = new MemoryStream();
        var oversized = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            var start = 0;

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                if (!oversized)
                {
                    await line.WriteAsync(buffer.AsMemory(start, i - start), cancellationToken).ConfigureAwait(false);
                    yield return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                }

                line.SetLength(0);
                oversized = false;
                start = i + 1;
            }

            if (!oversized)
            {
                oversized = await AppendTrailingChunkAsync(line, buffer.AsMemory(start, read - start), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<bool> AppendTrailingChunkAsync(
        MemoryStream line,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        await line.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        if (line.Length > MaxLineBytes)
        {
            // 超長行整行作廢，直到下一個換行為止。
            line.SetLength(0);
            return true;
        }

        return false;
    }

    /// <summary>驗證並收下一行 NDJSON。任何一項不通過就安靜丟棄，不中斷連線。</summary>
    internal void Accept(string line, string? expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            // 沒有 token 檔就代表沒有進行中的場次，一律拒收。
            return;
        }

        JsonElement payload;

        try
        {
            payload = JsonDocument.Parse(line).RootElement;
        }
        catch (JsonException)
        {
            // 單一壞行不該拖垮整條連線。
            return;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!payload.TryGetProperty("token", out var token)
            || token.ValueKind != JsonValueKind.String
            || !TokenEquals(token.GetString(), expectedToken))
        {
            return;
        }

        if (!payload.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() is not { } typeText
            || !AcceptedTypes.Contains(typeText))
        {
            return;
        }

        var firstLine = payload.TryGetProperty("first_line", out var first) && first.ValueKind == JsonValueKind.String
            ? first.GetString() ?? string.Empty
            : string.Empty;

        var text = payload.TryGetProperty("text", out var body) && body.ValueKind == JsonValueKind.String
            ? body.GetString() ?? string.Empty
            : string.Empty;

        // 只取需要的欄位——token 到此為止，不會進入儲存或回應。
        diagnostics.Add(typeText, firstLine, text, "bridge", sessions.Read().RunId);
    }

    private static bool TokenEquals(string? provided, string expected) =>
        string.Equals(provided, expected, StringComparison.Ordinal);

    private string? ReadToken()
    {
        try
        {
            return File.ReadAllText(store.BridgeTokenFile).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
