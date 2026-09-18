using Microsoft.Extensions.DependencyInjection;
using RimWorldModMcp.Core.Paths;

namespace RimWorldModMcp.Diagnostics;

public static class ServiceCollectionExtensions
{
    /// <summary>登記診斷儲存、daemon 引導與測試循環服務。</summary>
    public static IServiceCollection AddRimWorldDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<DiagnosticStore>();
        services.AddSingleton<GameStateStore>();
        services.AddSingleton<TestSessionStore>();
        services.AddSingleton<DaemonRecordStore>();
        services.AddSingleton<DaemonBootstrapper>();
        services.AddSingleton<BridgeBuilder>();
        services.AddSingleton(sp => new DaemonListener(
            sp.GetRequiredService<StoreDirectories>(),
            sp.GetRequiredService<RimWorldLocator>().BridgePort(),
            sp.GetRequiredService<DiagnosticStore>(),
            sp.GetRequiredService<TestSessionStore>(),
            sp.GetRequiredService<DaemonRecordStore>(),
            sp.GetRequiredService<GameStateStore>()));
        services.AddSingleton<TestEnvironmentPreparer>();
        services.AddSingleton<GameLauncher>();
        services.AddSingleton<TestCycleService>();

        return services;
    }
}
