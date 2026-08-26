using Microsoft.Extensions.DependencyInjection;

namespace RimWorldModMcp.Diagnostics;

public static class ServiceCollectionExtensions
{
    /// <summary>登記診斷儲存、daemon 引導與測試循環服務。</summary>
    public static IServiceCollection AddRimWorldDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<DiagnosticStore>();
        services.AddSingleton<TestSessionStore>();
        services.AddSingleton<DaemonRecordStore>();
        services.AddSingleton<DaemonBootstrapper>();
        services.AddSingleton<BridgeBuilder>();
        services.AddSingleton<DaemonListener>();
        services.AddSingleton<TestCycleService>();

        return services;
    }
}
