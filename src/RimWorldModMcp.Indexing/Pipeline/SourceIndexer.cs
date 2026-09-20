using System.Diagnostics;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>原始碼索引的進度。</summary>
public sealed record SourceIndexProgress(bool Running, bool Completed, int FilesIndexed, long ElapsedMilliseconds, string? Error);

/// <summary>
/// 第三層索引：把整個組件反編譯後存進資料庫供全文搜尋。
///
/// <para>
/// 這是唯一分鐘級的步驟，所以刻意不放在 rebuild_index 的同步流程裡。
/// Def 搜尋、符號查詢與單一成員反編譯都不依賴它，因此索引建好之後立刻可用；
/// 只有 search_source 需要等這一層完成。
/// </para>
/// <para>
/// 反編譯結果存進 source_file 表而不是落地成上萬個檔案——Python 版
/// 在快取目錄產生的檔案樹，每次 search_source 都要整個掃過一遍。
/// </para>
/// </summary>
public sealed class SourceIndexer(IndexDatabase database, RimWorldLocator locator)
{
    private readonly Lock _gate = new();
    private Task? _current;
    private CancellationTokenSource? _cancellation;
    private SourceIndexProgress _progress = new(false, false, 0, 0, null);

    /// <summary>每批寫入的檔案數。200 個反編譯檔約數 MB，交易只持續毫秒級。</summary>
    private const int BatchSize = 200;

    /// <summary>index_meta 鍵前綴：某個組件已完整索引，值是該 DLL 的 stamp。</summary>
    public const string DoneKeyPrefix = "source_done:";

    public SourceIndexProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return _progress;
            }
        }
    }

    /// <summary>
    /// 在背景啟動原始碼索引。已在執行中就直接返回目前進度，不會重複啟動。
    /// </summary>
    public SourceIndexProgress StartInBackground(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false })
            {
                return _progress;
            }

            // 保留 CTS 才有辦法叫停——沒有它，rebuild_index 清空 source_file 時
            // 上一輪背景索引還在往裡寫，而且沒有任何路徑能停止它。
            _cancellation?.Dispose();
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cancellation.Token;

            _progress = new SourceIndexProgress(true, false, 0, 0, null);
            _current = Task.Run(() => Run(token), token);

            return _progress;
        }
    }

    /// <summary>
    /// 取消進行中的背景索引並等它停下。沒有在跑時什麼都不做。
    /// </summary>
    public void Cancel()
    {
        Task? current;

        lock (_gate)
        {
            current = _current;
            _cancellation?.Cancel();
        }

        if (current is null)
        {
            return;
        }

        try
        {
            // 最多等一小段時間讓當前交易收尾；等不到也沒關係，
            // 呼叫端接下來的資料庫操作會靠 busy_timeout 與已取消的 token 收斂。
            current.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch (AggregateException)
        {
            // 取消或失敗的結果已經記錄在 _progress，這裡不需要重拋。
        }
    }

    /// <summary>同步執行原始碼索引，主要供測試與 CLI 使用。</summary>
    public SourceIndexProgress Run(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var indexed = 0;

        try
        {
            var paths = locator.Detect();

            if (paths.ManagedDir is null)
            {
                throw new DirectoryNotFoundException("RimWorld Managed directory not found.");
            }

            using var decompiler = new MemberDecompiler(locator);
            using var connection = database.Open();

            IndexMetaRepository.Set(connection, "source_index_error", string.Empty);

            foreach (var assembly in Directory.GetFiles(paths.ManagedDir, "Assembly-CSharp*.dll").Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(assembly);
                var stamp = MemberDecompiler.Stamp(assembly);
                var doneKey = DoneKeyPrefix + name;

                // 上一輪被中斷（行程重啟）時已經做完的組件直接沿用：反編譯 Assembly-CSharp
                // 是分鐘級的工作，沒有理由因為 firstpass 之後死掉就整個重來。
                if (IndexMetaRepository.Get(connection, doneKey) == stamp)
                {
                    indexed += (int)SourceFileRepository.CountAssembly(connection, name);
                    Report(new SourceIndexProgress(true, false, indexed, stopwatch.ElapsedMilliseconds, null));
                    continue;
                }

                // 反編譯在交易之外做、寫入用短交易分批：一個組件一個交易的話，
                // 寫鎖會被握住好幾分鐘，這段期間 Mod 的索引寫入會在 busy_timeout（5 秒）
                // 之後以 SQLITE_BUSY 失敗——而文件建議的流程正是 rebuild_index 之後
                // 馬上 search_source(package_id)。分批也讓中途失敗時已寫入的檔案得以保留。
                var batch = new List<(string Path, string Text)>(BatchSize);

                foreach (var file in decompiler.DecompileAll(assembly, cancellationToken))
                {
                    batch.Add(file);

                    if (batch.Count >= BatchSize)
                    {
                        indexed += Write(connection, name, batch);
                        Report(new SourceIndexProgress(true, false, indexed, stopwatch.ElapsedMilliseconds, null));
                    }
                }

                indexed += Write(connection, name, batch);
                IndexMetaRepository.Set(connection, doneKey, stamp);
            }

            // 收尾前再檢查一次取消。Rebuild 只等 Cancel 最多 10 秒
            //（見 SourceIndexer.Cancel），超時就放行並清空 source_file；
            // 這裡不檢查的話，被取消的任務會在 rebuild 提交 source_indexed=false
            // 之後把它改回 true，與剛被清空的表不符——呼叫端於是看到
            // 「已索引完成」卻搜不到任何東西。
            cancellationToken.ThrowIfCancellationRequested();

            // 用同一條連線寫 meta。開第二條連線時第一條還活著——WAL 下沒事，
            // 但快取目錄落在 OneDrive／網路磁碟時會退回 DELETE journal，
            // 兩條連線會互卡到 busy_timeout 之後拋 SQLITE_BUSY。
            IndexMetaRepository.Set(connection, "source_indexed", "true");
            IndexMetaRepository.Set(connection, "source_file_count", indexed.ToString());

            stopwatch.Stop();
            return Report(new SourceIndexProgress(false, true, indexed, stopwatch.ElapsedMilliseconds, null));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Report(new SourceIndexProgress(false, false, indexed, stopwatch.ElapsedMilliseconds, "cancelled"));
        }
        catch (Exception e)
        {
            stopwatch.Stop();
            PersistError(e.Message);
            return Report(new SourceIndexProgress(false, false, indexed, stopwatch.ElapsedMilliseconds, e.Message));
        }
    }

    private static int Write(Microsoft.Data.Sqlite.SqliteConnection connection, string assembly, List<(string Path, string Text)> batch)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        using (var transaction = connection.BeginTransaction())
        {
            foreach (var (path, text) in batch)
            {
                SourceFileRepository.Insert(connection, assembly, path, text);
            }

            transaction.Commit();
        }

        var written = batch.Count;
        batch.Clear();
        return written;
    }

    /// <summary>
    /// 尚未完成、也沒有失敗紀錄的原始碼索引是「被中斷」的（多半是行程重啟）。
    /// 呼叫端據此決定要不要在啟動時續跑。
    /// CountGame ＞ 0 的取捨：第一批 200 檔寫入前就中斷的話，資料表是空的，
    /// 這裡回 false，啟動時不續跑——那些檔案在 rebuild_index 之後本來就會重跑，
    /// 沒有「只差一點就完成」的便宜續跑可省；要讓它把「從零開始」也當續跑的話，
    /// 每次啟動都會多跑一次背景索引，而正常完成後 source_indexed=true 根本不會
    /// 走到這裡。回 false 的後果只是 rimworld_status 顯示「未建」而非「中斷」，
    /// 呼叫端 rebuild_index 即可。
    /// </summary>
    public bool IsInterrupted()
    {
        using var connection = database.Open();

        return IndexMetaRepository.Get(connection, "fingerprint") is { Length: > 0 }
            && IndexMetaRepository.Get(connection, "source_indexed") != "true"
            && string.IsNullOrEmpty(IndexMetaRepository.Get(connection, "source_index_error"))
            && SourceFileRepository.CountGame(connection) > 0;
    }

    /// <summary>
    /// 把失敗原因寫進 index_meta。行程內的 _progress 在重啟後就消失了，
    /// 沒有這一步，使用者只會看到 source_indexed 一直是 false 而永遠等不到原因。
    /// </summary>
    private void PersistError(string message)
    {
        try
        {
            using var connection = database.Open();
            IndexMetaRepository.Set(connection, "source_index_error", message);
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // 連錯誤都寫不進去（資料庫本身壞了）時，_progress 至少還在。
        }
    }

    private SourceIndexProgress Report(SourceIndexProgress progress)
    {
        lock (_gate)
        {
            _progress = progress;
            return progress;
        }
    }
}
