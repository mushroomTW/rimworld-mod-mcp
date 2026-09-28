using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    GameStateStore gameState,
    TimeSpan? readTimeout = null)
{
    /// <summary>只接受這些訊息類型，其餘一律丟棄。</summary>
    private static readonly HashSet<string> AcceptedTypes = new(StringComparer.Ordinal)
    {
        "error", "warning", "diagnostic", "loaded_mods", "performance",
    };

    /// <summary>遊戲狀態不是診斷：不去重、不累計，只覆蓋成最新一份。</summary>
    private const string GameStateType = "game_state";

    /// <summary>open_windows 的筆數上限；Bridge 端也截，這裡再守一次。</summary>
    private const int MaxOpenWindows = 8;

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
    /// 先 bind 再寫自述檔：綁定成功才代表這個埠真的是我們的。
    /// 反過來寫的話，另一個程序佔著埠時我們仍會留下自述檔，
    /// 讓後續的判斷誤以為埠是自己人佔的。
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 日誌監看必須獨立於 TCP 綁定：埠被佔用時仍要收集 Player.log，
        // 否則 Bridge 與 Player.log 診斷會同時遺失（F02）。
        var tailer = new PlayerLogTailer(sessions, diagnostics);
        var tailing = tailer.RunAsync(cancellationToken);

        TcpListener? listener = null;

        try
        {
            listener = new TcpListener(IPAddress.Loopback, bridgePort);

            try
            {
                listener.Start();
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // 埠被其他程式佔用：進入僅日誌模式，Bridge 不可用但 Player.log 仍 tail。
                // 自述檔照寫，Bootstrapper 才能認出這是自己人並回報降級狀態，而不是誤報 unavailable。
                records.Write(new DaemonRecord
                {
                    Pid = Environment.ProcessId,
                    Port = bridgePort,
                    StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    StartTimeUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                    Mode = DaemonRecord.ModeLogOnly,
                    Reason = $"Port {bridgePort} is held by a non-service process; the Bridge cannot report diagnostics. Player.log tailing remains active.",
                });

                await tailing;
                return;
            }

            records.Write(new DaemonRecord
            {
                Pid = Environment.ProcessId,
                Port = bridgePort,
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                StartTimeUtc = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                Mode = DaemonRecord.ModeFull,
            });

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
            listener?.Stop();
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

                // run_id 同理：連線與遊戲行程同生共死，一條連線只屬於一個場次。
                // 每行都讀一次狀態檔的話，錯誤風暴時就是每行一次磁碟讀取。
                var runId = sessions.Read().RunId;

                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(_readTimeout);

                await foreach (var line in ReadLinesAsync(client.GetStream(), timeoutSource.Token).ConfigureAwait(false))
                {
                    // 處理期間暫停計時：寫診斷檔在負載下可能很慢，算進閒置的話
                    // 計時會在處理途中到期，下一次讀取直接被取消而切斷連線。
                    timeoutSource.CancelAfter(Timeout.InfiniteTimeSpan);
                    Accept(line, expected, runId);

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

    /// <summary>以目前場次的 run_id 收下一行。</summary>
    internal void Accept(string line, string? expectedToken) => Accept(line, expectedToken, sessions.Read().RunId);

    /// <summary>驗證並收下一行 NDJSON。任何一項不通過就安靜丟棄，不中斷連線。</summary>
    internal void Accept(string line, string? expectedToken, string? runId)
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
            || type.GetString() is not { } typeText)
        {
            return;
        }

        if (typeText == GameStateType)
        {
            HandleGameState(payload, runId);
            return;
        }

        HandleDiagnostic(payload, typeText, runId);
    }

    private void HandleGameState(JsonElement payload, string? runId)
    {
        try
        {
            gameState.Write(ToGameState(payload, runId));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows 上 server 正在讀這個檔時置換會失敗。丟掉這一筆就好——
            // 下一筆一秒內就到；讓例外逸出會被當成連線死亡而切斷整條 Bridge 連線。
        }
    }

    private void HandleDiagnostic(JsonElement payload, string typeText, string? runId)
    {
        if (!AcceptedTypes.Contains(typeText))
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
        diagnostics.Add(typeText, firstLine, text, "bridge", runId);
    }

    /// <summary>每個欄位各自容錯：Bridge 版本較舊而少送某個欄位時，其餘欄位仍然可用。</summary>
    private static GameStateRecord ToGameState(JsonElement payload, string? runId) => new()
    {
        ProgramState = ReadString(payload, "program_state") ?? "Unknown",
        MapLoaded = ReadBool(payload, "map_loaded"),
        Tick = ReadInt(payload, "tick"),
        Paused = ReadBool(payload, "paused"),
        TimeSpeed = ReadString(payload, "time_speed"),
        Loading = ReadBool(payload, "loading"),
        OpenWindows = ReadStrings(payload, "open_windows"),
        Colonists = ReadInt(payload, "colonists"),
        GameVersion = ReadString(payload, "game_version"),
        UptimeMs = payload.TryGetProperty("uptime_ms", out var uptime) && uptime.ValueKind == JsonValueKind.Number && uptime.TryGetInt64(out var ms) ? ms : 0,
        RunId = runId,
        At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    private static string? ReadString(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadBool(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // TryGetInt32 在非數字節點上會拋而不是回 false，所以先檢查 ValueKind。
    private static int ReadInt(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static IReadOnlyList<string> ReadStrings(JsonElement payload, string name)
        => payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Take(MaxOpenWindows)]
            : [];

    /// <summary>
    /// 固定時間比較，避免以回應時間逐位元組推測 token。
    ///
    /// <para>
    /// 實務上難以利用（只綁 loopback、token 是 192 位元隨機），但這是驗證程式碼，
    /// 用正確的原語成本為零。長度不同時 <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// 直接回 false——長度本身不是秘密。
    /// </para>
    /// </summary>
    private static bool TokenEquals(string? provided, string expected)
    {
        if (provided is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
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
