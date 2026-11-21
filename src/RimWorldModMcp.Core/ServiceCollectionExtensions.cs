using Microsoft.Extensions.DependencyInjection;
using RimWorldModMcp.Core.Locking;
using RimWorldModMcp.Core.Mods;
using RimWorldModMcp.Core.Paths;
using RimWorldModMcp.Core.Platform;

namespace RimWorldModMcp.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>登記平台層、路徑偵測、鎖與 Mod 目錄服務。</summary>
    public static IServiceCollection AddRimWorldCore(this IServiceCollection services)
    {
        services.AddSingleton<StoreDirectories>();
        services.AddSingleton<RimWorldLocator>();
        services.AddSingleton<IProcessHost, ProcessHost>();
        services.AddSingleton<CriticalSectionLock>();
        services.AddSingleton<ModCatalog>();

        return services;
    }
}
