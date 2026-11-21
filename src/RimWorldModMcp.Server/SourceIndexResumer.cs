using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModMcp.Indexing.Pipeline;

namespace RimWorldModMcp.Server;

/// <summary>
/// 啟動時在背景確認索引可用：過期或損毀就重建，原始碼索引被中斷就續跑。
///
/// <para>
/// 第三層索引在背景跑好幾分鐘，MCP client 一關 server 就跟著死；遊戲更新後
/// 第一層索引也會過期。這些都交給 <see cref="IndexAutoRebuild"/>，
/// 這裡只是讓它在第一次查詢之前就先跑，查詢不必等重建。
/// </para>
/// </summary>
internal sealed class SourceIndexResumer(
    IndexAutoRebuild autoRebuild,
    SourceIndexer indexer,
    ILogger<SourceIndexResumer> logger) : IHostedService
{
    private Task _startup = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // 判斷要開資料庫（含 quick_check）並算一次指紋，重建還要幾秒，不能擋住 initialize 交握。
        _startup = Task.Run(() =>
        {
            try
            {
                autoRebuild.EnsureReady();
            }
            catch (Exception e)
            {
                // 啟動期的檢查失敗不該影響 server；工具呼叫時會再各自遇到並回報。
                logger.LogWarning(e, "Could not prepare the index at startup.");
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 等啟動檢查收尾，否則它可能在下面叫停之後才啟動背景索引。
        try
        {
            await _startup.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 主機要求立刻停止：不再等，照樣叫停背景索引。
        }

        // 讓進行中的批次收尾（最多等 10 秒），而不是在交易中途被行程結束切斷。
        indexer.Cancel();
    }
}
