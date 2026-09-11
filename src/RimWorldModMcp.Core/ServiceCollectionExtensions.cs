using Microsoft.Extensions.DependencyInjection;
using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 登記平台層、路徑偵測、鎖與工作區服務。
    /// <paramref name="workspaces"/> 是本次啟動信任的工作區根目錄，見 <see cref="WorkspaceRegistry"/>。
    /// </summary>
    public static IServiceCollection AddRimWorldCore(this IServiceCollection services, IReadOnlyList<string> workspaces)
    {
        services.AddSingleton<StoreDirectories>();
        services.AddSingleton<IRimWorldLocator, RimWorldLocator>();
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<DirectoryLink>();
        services.AddSingleton<CriticalSectionLock>();
        services.AddSingleton(new WorkspaceRegistry(workspaces));
        services.AddSingleton<ModScaffolder>();
        services.AddSingleton<ModCatalog>();
        services.AddSingleton<LoadOrderResolver>();
        services.AddSingleton<BuildService>();

        return services;
    }
}
