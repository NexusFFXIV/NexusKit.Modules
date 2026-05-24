using Microsoft.Extensions.DependencyInjection;
using NexusKit.Modules.InternalData.Encounters;
using NexusKit.Modules.InternalData.History;
using NexusKit.Modules.InternalData.Persistence;
using NexusKit.Modules.InternalData.Players;
using NexusKit.Persistence;

namespace NexusKit.Modules.InternalData;

public static class InternalDataServiceCollectionExtensions
{
    /// <summary>
    /// Registers the InternalData module: the in-game player watcher plus the
    /// observation entity module that persists the watcher's snapshots into the
    /// plugin DB. The watcher subscribes to <c>IFramework.Update</c> in its
    /// constructor, so plugin composition should eagerly resolve it once at startup
    /// (analogous to how PluginUiHost is resolved to wire its event handlers).
    /// <para>This module deliberately knows nothing about Lodestone / FFXIVCollect.
    /// The bridge to those data sources (Lodestone-id resolution, refresh queue
    /// worker) lives in <c>NexusKit.Modules.PlayerEnrichment</c>, which references
    /// both <c>InternalData</c> and <c>ExternalData</c> — that's the only place
    /// where the two modules are allowed to meet.</para>
    /// </summary>
    public static IServiceCollection AddNexusKitInternalData(this IServiceCollection services)
    {
        services.AddEntityModule<InternalDataEntityModule>();
        services.AddMigrationModule<InternalDataMigrations>();
        services.AddViewBuilder<PlayerFilterViewBuilder>();

        services.AddSingleton<InternalDataPlayerWatcher>();
        services.AddSingleton<IInternalDataPlayerWatcher>(sp => sp.GetRequiredService<InternalDataPlayerWatcher>());

        services.AddSingleton<InternalDataHistoryService>();
        services.AddSingleton<IInternalDataHistoryService>(sp => sp.GetRequiredService<InternalDataHistoryService>());

        services.AddSingleton<InternalDataEncounterTracker>();
        services.AddSingleton<IInternalDataEncounterTracker>(sp => sp.GetRequiredService<InternalDataEncounterTracker>());

        return services;
    }
}
