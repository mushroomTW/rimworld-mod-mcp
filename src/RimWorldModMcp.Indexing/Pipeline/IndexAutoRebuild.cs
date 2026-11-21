using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Indexing.Pipeline;

/// <summary>
/// 查詢前確認索引可用，必要時自動重建，取代過去要由呼叫端手動觸發的 rebuild_index。
///
/// <para>
/// 第一層索引（Def 與符號）過期（遊戲更新過）或資料庫損毀時，同步重建——只要幾秒，
/// 查詢工具接著就能用；原始碼全文索引則在背景補建。原始碼索引沒建完又沒在跑
/// （行程重啟中斷、或從沒啟動）時也在背景開始。
/// </para>
/// <para>
/// 原始碼索引失敗過的話，每個行程只自動重試一次：那是好幾分鐘的反編譯，
/// 每次查詢都重跑一個註定失敗的工作只會拖垮機器。
/// </para>
/// </summary>
public sealed class IndexAutoRebuild(
    RimWorldLocator locator,
    IndexBuilder builder,
    SourceIndexer sourceIndexer,
    ModInspectionService inspection)
{
    /// <summary>
    /// 兩次檢查的最短間隔。檢查要算指紋（對兩萬個 Def 檔各 stat 一次，幾百毫秒到數秒），
    /// 每次查詢都算太貴；遊戲在一次工作途中更新很少見，一分鐘內發現就夠。
    /// </summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private DateTime _checkedAtUtc = DateTime.MinValue;
    private bool _retriedSourceError;

    /// <summary>確認索引可用；過期或損毀時先重建。同時只有一個呼叫者會重建，其餘等它完成。</summary>
    public void EnsureReady()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _checkedAtUtc < CheckInterval)
            {
                return;
            }

            // 偵測不到遊戲或是不支援的舊版：重建一定失敗，讓查詢照常回報原因。
            if (!locator.Detect().HasManagedAndData)
            {
                return;
            }

            var status = builder.Status();

            // 另一個行程正在寫：不記下檢查時間，下一次查詢再看。
            if (status.Busy)
            {
                return;
            }

            if (!status.Healthy || !status.Fresh)
            {
                try
                {
                    builder.Rebuild();
                }
                catch (Exception e) when (e is LockHeldException or IOException or UnauthorizedAccessException)
                {
                    // 另一個行程正在重建，或遊戲正在更新、檔案被鎖：暫時性的。
                    // 讓這次查詢照舊用現有索引，一個檢查間隔後再試，而不是每個查詢工具都跟著失敗。
                    _checkedAtUtc = DateTime.UtcNow;
                    return;
                }

                // 重建（可能連同重設）之後，Mod 背景索引先前記下的失敗原因已經過時，
                // 新世代的原始碼索引也重新擁有一次自動重試。
                inspection.ForgetErrors();
                _retriedSourceError = false;
                sourceIndexer.StartInBackground();
            }
            else if (!status.SourceIndexed && !status.SourceIndex.Running
                && (status.SourceIndex.Error is null || !_retriedSourceError))
            {
                _retriedSourceError |= status.SourceIndex.Error is not null;
                sourceIndexer.StartInBackground();
            }

            _checkedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>讓下一次 <see cref="EnsureReady"/> 立刻重新檢查，例如查詢時發現索引與組件對不上。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _checkedAtUtc = DateTime.MinValue;
        }
    }
}
