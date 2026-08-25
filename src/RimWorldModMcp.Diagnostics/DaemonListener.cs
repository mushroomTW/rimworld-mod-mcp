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
    IRimWorldLocator locator,
    DiagnosticStore diagnostics,
    TestSessionStore sessions,
    DaemonRecordStore records)
{
    /// <summary>只接受這些訊息類型，其餘一律丟棄。</summary>
    private static readonly HashSet<string> AcceptedTypes = new(StringComparer.Ordinal)
    {
        "error", "warning", "diagnostic", "loaded_mods", "performance",
    };

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
        var port = locator.BridgePort();
        var listener = new TcpListener(IPAddress.Loopback, port);

        listener.Start();

        try
        {
            records.Write(new DaemonRecord
            {
                Pid = Environment.ProcessId,
                Port = port,
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
            // token 在每個連線建立時讀一次。Bridge 每送一筆診斷就開一條新連線，
            // 所以場次之間換 token 能立刻生效。
            var expected = ReadToken();

            try
            {
                using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));

                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    Accept(line, expected);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException)
            {
                // 連線中斷是常態（遊戲關閉、寫到一半退出），不需要處理。
            }
        }
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
            || !string.Equals(token.GetString(), expectedToken, StringComparison.Ordinal))
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
