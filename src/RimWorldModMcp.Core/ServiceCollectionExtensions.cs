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
    /// <summary>登記平台層、路徑偵測、鎖、Mod 骨架與建置服務。</summary>
    public static IServiceCollection AddRimWorldCore(this IServiceCollection services)
    {
        services.AddSingleton<StoreDirectories>();
        services.AddSingleton<RimWorldLocator>();
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<DirectoryLink>();
        services.AddSingleton<CriticalSectionLock>();
        services.AddSingleton<ModScaffolder>();
        services.AddSingleton<ModCatalog>();
        services.AddSingleton<LoadOrderResolver>();
        services.AddSingleton<BuildService>();

        return services;
    }
}
