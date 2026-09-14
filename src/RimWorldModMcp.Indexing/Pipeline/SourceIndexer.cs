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
/// 這是唯一分鐘級的步驟，所以刻意不放在 <c>rebuild_index</c> 的同步流程裡。
/// Def 搜尋、符號查詢與單一成員反編譯都不依賴它，因此索引建好之後立刻可用；
/// 只有 <c>search_source</c> 需要等這一層完成。
/// </para>
/// <para>
/// 反編譯結果存進 <c>source_file</c> 表而不是落地成上萬個檔案——Python 版
/// 在快取目錄產生的檔案樹，每次 <c>search_source</c> 都要整個掃過一遍。
/// </para>
/// </summary>
public sealed class SourceIndexer(IndexDatabase database, RimWorldLocator locator)
{
    private readonly Lock _gate = new();
    private Task? _current;
    private CancellationTokenSource? _cancellation;
    private SourceIndexProgress _progress = new(false, false, 0, 0, null);

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

            using var decompiler = new MemberDecompiler();
            using var connection = database.Open();

            IndexMetaRepository.Set(connection, "source_index_error", string.Empty);

            foreach (var assembly in Directory.GetFiles(paths.ManagedDir, "Assembly-CSharp*.dll").Order())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileNameWithoutExtension(assembly);

                // 每個組件各自一個交易：中途失敗時已完成的組件仍然保留，
                // 不會因為最後一個組件出問題而整批白做。
                using var transaction = connection.BeginTransaction();

                foreach (var (path, text) in decompiler.DecompileAll(assembly, cancellationToken))
                {
                    SourceFileRepository.Insert(connection, name, path, text);
                    indexed++;

                    if (indexed % 500 == 0)
                    {
                        Report(new SourceIndexProgress(true, false, indexed, stopwatch.ElapsedMilliseconds, null));
                    }
                }

                transaction.Commit();
            }

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
