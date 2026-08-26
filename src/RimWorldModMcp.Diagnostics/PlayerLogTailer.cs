using System.Text;

namespace RimWorldModMcp.Diagnostics;

/// <summary>
/// 持續讀取 RimWorld 的 Player.log，把錯誤與警告收進診斷。
///
/// <para>
/// Bridge 連不上時（埠被佔、Mod 沒載入成功）這是唯一的診斷來源，
/// 所以即使 Bridge 正常也一併讀——兩邊的資訊互補。
/// </para>
/// </summary>
public sealed class PlayerLogTailer(TestSessionStore sessions, DiagnosticStore diagnostics)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // 以路徑為鍵記住讀到哪裡。
        var offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        string? currentRun = null;

        using var timer = new PeriodicTimer(PollInterval);

        while (await SafeWaitAsync(timer, cancellationToken))
        {
            var session = sessions.Read();

            // 換一輪測試就丟掉舊的位置，否則第二輪會從第一輪的結尾開始而漏讀。
            if (session.RunId != currentRun)
            {
                currentRun = session.RunId;
                offsets.Clear();
            }

            if (session.State != "running" || string.IsNullOrEmpty(session.PlayerLog) || !File.Exists(session.PlayerLog))
            {
                continue;
            }

            ReadNewLines(session, offsets);
        }
    }

    private void ReadNewLines(TestSession session, Dictionary<string, long> offsets)
    {
        var path = session.PlayerLog!;

        if (!offsets.TryGetValue(path, out var offset))
        {
            // 第一次讀這個檔案時，從啟動遊戲前記下的位置開始，
            // 才不會把上一輪遊戲留下的內容當成這次的診斷。
            offset = session.LogOffset;
        }

        long length;

        try
        {
            length = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return;
        }

        // 檔案變短代表遊戲重啟後截斷了 log，位置要歸零重讀。
        if (length < offset)
        {
            offset = 0;
        }

        if (length <= offset)
        {
            return;
        }

        try
        {
            // FileShare 必須含 Delete，否則會擋住遊戲輪替 log 檔。
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            while (reader.ReadLine() is { } line)
            {
                Classify(line, session.RunId);
            }

            offsets[path] = stream.Position;
        }
        catch (IOException)
        {
            // 遊戲正在寫入時偶爾會讀不到，下一輪再試。
        }
    }

    private void Classify(string line, string? runId)
    {
        var text = line.Trim();

        if (text.Length == 0)
        {
            return;
        }

        var isError = text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase);

        var isWarning = text.Contains("warning", StringComparison.OrdinalIgnoreCase);

        if (!isError && !isWarning)
        {
            return;
        }

        diagnostics.Add(isError ? "error" : "warning", text, text, "player.log", runId);
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
