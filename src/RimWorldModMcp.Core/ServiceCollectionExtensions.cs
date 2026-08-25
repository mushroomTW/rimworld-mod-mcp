using Microsoft.Extensions.DependencyInjection;
using RimWorldModMcp.Core.Assets;
using RimWorldModMcp.Core.Building;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;
using RimWorldModMcp.Core.Workspace;

namespace RimWorldModMcp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>登記平台層、路徑偵測、鎖與工作區服務。</summary>
    public static IServiceCollection AddRimWorldCore(this IServiceCollection services)
    {
        services.AddSingleton<StoreDirectories>();
        services.AddSingleton<IRimWorldLocator, RimWorldLocator>();
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<IDirectoryLink, DirectoryLink>();
        services.AddSingleton<CriticalSectionLock>();
        services.AddSingleton<WorkspaceRegistry>();
        services.AddSingleton<ModScaffolder>();
        services.AddSingleton<CheckpointService>();
        services.AddSingleton<ModCatalog>();
        services.AddSingleton<LoadOrderResolver>();
        services.AddSingleton<AssetService>();
        services.AddSingleton<BuildService>();

        return services;
    }
}
