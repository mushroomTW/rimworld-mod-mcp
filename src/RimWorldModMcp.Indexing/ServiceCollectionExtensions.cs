using Microsoft.Extensions.DependencyInjection;
using RimWorldModMcp.Indexing.Decompilation;
using RimWorldModMcp.Indexing.Pipeline;
using RimWorldModMcp.Indexing.Query;
using RimWorldModMcp.Indexing.Storage;

namespace RimWorldModMcp.Indexing;

public static class ServiceCollectionExtensions
{
    /// <summary>登記索引資料庫、建置管線與查詢服務。</summary>
    public static IServiceCollection AddRimWorldIndexing(this IServiceCollection services)
    {
        services.AddSingleton<IndexDatabase>();
        services.AddSingleton<IndexFingerprint>();
        services.AddSingleton<IndexBuilder>();
        services.AddSingleton<SourceIndexer>();
        services.AddSingleton<ModInspectionService>();

        // 反編譯器持有組件的解析狀態，重複建立成本高（30MB 組件約 2.7 秒），
        // 所以做成單例讓初始化只付一次。內部對每個組件各自上鎖。
        services.AddSingleton<MemberDecompiler>();

        return services;
    }
}
