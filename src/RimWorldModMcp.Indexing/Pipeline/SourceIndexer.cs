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
public sealed class SourceIndexer(IndexDatabase database, IRimWorldLocator locator)
{
    private readonly Lock _gate = new();
    private Task? _current;
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

            _progress = new SourceIndexProgress(true, false, 0, 0, null);
            _current = Task.Run(() => Run(cancellationToken), cancellationToken);

            return _progress;
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
                throw new DirectoryNotFoundException("找不到 RimWorld 的 Managed 目錄。");
            }

            using var decompiler = new MemberDecompiler();
            using var connection = database.Open();

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

            using (var final = database.Open())
            {
                IndexMetaRepository.Set(final, "source_indexed", "true");
                IndexMetaRepository.Set(final, "source_file_count", indexed.ToString());
            }

            stopwatch.Stop();
            return Report(new SourceIndexProgress(false, true, indexed, stopwatch.ElapsedMilliseconds, null));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Report(new SourceIndexProgress(false, false, indexed, stopwatch.ElapsedMilliseconds, "已取消"));
        }
        catch (Exception e)
        {
            stopwatch.Stop();
            return Report(new SourceIndexProgress(false, false, indexed, stopwatch.ElapsedMilliseconds, e.Message));
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
