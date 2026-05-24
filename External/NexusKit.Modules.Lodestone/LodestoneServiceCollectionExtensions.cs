using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusKit.Core.Cache;
using NexusKit.Core.Ipc;
using NexusKit.Core.Localization;
using NexusKit.Modules.Lodestone.Cache;
using NexusKit.Modules.Lodestone.Clients;
using NexusKit.Modules.Lodestone.Ipc;
using NexusKit.Modules.Lodestone.Maintenance;
using NexusKit.Modules.Lodestone.Persistence;
using NexusKit.Modules.Lodestone.Resources;
using NexusKit.Persistence;
using NexusKit.Persistence.Schema;
using NexusKit.Persistence.Settings.Schema;

namespace NexusKit.Modules.Lodestone;

public static class LodestoneServiceCollectionExtensions
{
    public static IServiceCollection AddNexusKitLodestone(this IServiceCollection services)
    {
        services.AddSettings<LodestoneSettings>(b => b
            .StoredAs(LodestoneSettings.StoreKey)
            .GroupKey("nexuskit.module.group", order: 200)
            .TitleKey("nexuskit.modules.lodestone.title")
            .RegisterModuleEnabledFlag(order: 0)
            .Property(x => x.CacheEnabled, p => p
                .LabelKey("nexuskit.modules.lodestone.cache_enabled.label")
                .DescriptionKey("nexuskit.modules.lodestone.cache_enabled.description")
                .Order(1))
            .Property(x => x.CacheTtlHours, p => p
                .LabelKey("nexuskit.modules.lodestone.cache_ttl.label")
                .DescriptionKey("nexuskit.modules.lodestone.cache_ttl.description")
                .Slider(1, 168)
                .Order(2)));

        services.AddEntityModule<LodestoneCacheEntityModule>();
        services.AddMaintenanceContributor<LodestoneCacheMaintenanceContributor>();
        services.AddResourceLocalizer<Strings>();
        // LocalizationManager drives region selection (de./fr./jp./eu.); idempotent so
        // multiple modules / the Ui layer can all add it without conflict.
        services.TryAddSingleton<LocalizationManager>();
        services.AddSingleton<ILodestoneClient, LodestoneClient>();

        // Cache-eviction surface — registered as one of potentially many
        // IExternalDataCacheResetter implementations, drained together by
        // the refresh-queue self-heal path.
        services.AddSingleton<IExternalDataCacheResetter, LodestoneCacheResetter>();

        // IPC provider — eagerly resolved by PluginHostBuilder.
        services.AddSingleton<IIpcProvider, LodestoneIpcProvider>();

        return services;
    }
}