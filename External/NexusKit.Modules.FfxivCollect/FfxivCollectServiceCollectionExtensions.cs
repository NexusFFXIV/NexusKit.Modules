using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusKit.Core.Cache;
using NexusKit.Core.Ipc;
using NexusKit.Core.Localization;
using NexusKit.Modules.FfxivCollect.Cache;
using NexusKit.Modules.FfxivCollect.Clients;
using NexusKit.Modules.FfxivCollect.Ipc;
using NexusKit.Modules.FfxivCollect.Maintenance;
using NexusKit.Modules.FfxivCollect.Persistence;
using NexusKit.Modules.FfxivCollect.Resources;
using NexusKit.Persistence;
using NexusKit.Persistence.Schema;
using NexusKit.Persistence.Settings.Schema;

namespace NexusKit.Modules.FfxivCollect;

public static class FfxivCollectServiceCollectionExtensions
{
    public static IServiceCollection AddNexusKitFfxivCollect(this IServiceCollection services)
    {
        // HTTP — named client so we can configure UA / Accept once and share across calls.
        services.AddHttpClient(FfxivCollectClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("NexusKit.Modules.FfxivCollect/0.1");
            c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // Schema (settings table) + cache (own DB entity contributed via IEntityModule).
        services.AddSettings<FfxivCollectSettings>(b => b
            .StoredAs(FfxivCollectSettings.StoreKey)
            .GroupKey("nexuskit.module.group", order: 100)
            .TitleKey("nexuskit.modules.ffxivcollect.title")
            .RegisterModuleEnabledFlag(order: 0)
            .Property(x => x.BaseUrl, p => p
                .LabelKey("nexuskit.modules.ffxivcollect.base_url.label")
                .DescriptionKey("nexuskit.modules.ffxivcollect.base_url.description")
                .PlaceholderKey("nexuskit.modules.ffxivcollect.base_url.placeholder")
                .Hidden()
                .Order(1))
            .Property(x => x.CacheEnabled, p => p
                .LabelKey("nexuskit.modules.ffxivcollect.cache_enabled.label")
                .DescriptionKey("nexuskit.modules.ffxivcollect.cache_enabled.description")
                .Order(2))
            .Property(x => x.CacheTtlHours, p => p
                .LabelKey("nexuskit.modules.ffxivcollect.cache_ttl.label")
                .DescriptionKey("nexuskit.modules.ffxivcollect.cache_ttl.description")
                .Slider(1, 168)
                .Order(3)));

        services.AddEntityModule<FfxivCollectCacheEntityModule>();
        services.AddMaintenanceContributor<FfxivCollectCacheMaintenanceContributor>();

        // Module-shipped translations (Strings.resx / Strings.de.resx). Plugin can still
        // override individual keys by registering a higher-priority ILocalizationSource.
        services.AddResourceLocalizer<Strings>();

        // LocalizationManager drives ?lang= selection on outgoing FFXIVCollect requests.
        // TryAdd so the module is safe to register without NexusKit.Ui present.
        services.TryAddSingleton<LocalizationManager>();
        services.AddSingleton<IFfxivCollectClient, FfxivCollectClient>();

        // Cache-eviction surface — same shared abstraction the Lodestone
        // module implements; PlayerEnrichment's self-heal drains every
        // registered resetter in one shot.
        services.AddSingleton<IExternalDataCacheResetter, FfxivCollectCacheResetter>();

        // IPC provider — exposes our endpoints as IPCs (JSON results) so foreign
        // plugins can consume them without depending on our types. Resolved
        // eagerly by PluginHostBuilder so registrations happen at startup.
        services.AddSingleton<IIpcProvider, FfxivCollectIpcProvider>();

        return services;
    }
}