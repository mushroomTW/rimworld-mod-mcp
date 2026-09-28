using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Server;

/// <summary>
/// 啟動時續跑被中斷的原始碼索引。
///
/// <para>
/// 第三層索引在背景跑好幾分鐘，MCP client 一關 server 就跟著死，下一個場次
/// 只會看到 source_indexed=false、沒有在跑、也沒有錯誤——唯一的出路是
/// 再叫一次 rebuild_index，而那會連第一層與所有 Mod 的索引一起清掉重來。
/// 這裡把「使用者已經要求過、只是沒做完」的工作接著做完；出過錯的不碰。
/// </para>
/// </summary>
internal sealed class SourceIndexResumer(
    SourceIndexer indexer,
    IndexBuilder builder,
    ILogger<SourceIndexResumer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 判斷要開資料庫（含 quick_check）並算一次指紋，不能擋住 initialize 交握。
        _ = Task.Run(() =>
        {
            try
            {
                // 遊戲更新過的話第一層索引已經過期，續跑第三層只是浪費；等使用者 rebuild_index。
                if (!builder.Status().Fresh || !indexer.IsInterrupted())
                {
                    return;
                }

                logger.LogInformation("Resuming the interrupted source index in the background.");
                indexer.StartInBackground(cancellationToken);
            }
            catch (Exception e)
            {
                // 啟動期的檢查失敗不該影響 server；工具呼叫時會再各自遇到並回報。
                logger.LogWarning(e, "Could not check whether the source index needs resuming.");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 讓進行中的批次收尾（最多等 10 秒），而不是在交易中途被行程結束切斷。
        indexer.Cancel();
        return Task.CompletedTask;
    }
}
