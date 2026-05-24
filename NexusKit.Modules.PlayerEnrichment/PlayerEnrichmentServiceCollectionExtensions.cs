using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusKit.Core.Localization;
using NexusKit.Modules.ExternalData;
using NexusKit.Modules.ExternalData.Players;
using NexusKit.Modules.InternalData;
using NexusKit.Modules.PlayerEnrichment.Bridges;
using NexusKit.Modules.PlayerEnrichment.Maintenance;
using NexusKit.Persistence;
using NexusKit.Ui.AutoSettings;

namespace NexusKit.Modules.PlayerEnrichment;

public static class PlayerEnrichmentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cross-cutting layer between <c>NexusKit.Modules.InternalData</c>
    /// and <c>NexusKit.Modules.ExternalData</c>. Composes both module bundles so
    /// callers (plugins) only depend on this assembly: it pulls in the live
    /// observation pipeline, the remote data sources, and the bridging services
    /// (Lodestone-id resolver, refresh queue) in one call.
    /// <para>The Lodestone-id resolver and refresh queue worker both spin up
    /// background work in their constructors; the plugin is expected to eagerly
    /// resolve them at startup so the threads start running on plugin load
    /// rather than first UI access.</para>
    /// </summary>
    public static IServiceCollection AddNexusKitPlayerEnrichment(this IServiceCollection services)
    {
        services.AddNexusKitInternalData();
        services.AddNexusKitExternalData();

        // Bridge: Lodestone-detected player changes → InternalData history log.
        // ExternalData defines IPlayerChangeRecorder as an optional seam; the
        // implementation lives here because PlayerEnrichment is the only
        // assembly allowed to know both module bundles.
        services.AddSingleton<IPlayerChangeRecorder, HistoryChangeRecorder>();

        // Bridge: live FC-tag flips fast-track a Profile + FreeCompany refresh
        // so the strong-match Lodestone diff produces an unambiguous
        // FreeCompanyChange history row in seconds instead of waiting for the
        // next TTL sweep. Eager-resolved at startup (see Plugin.LoadAsync) so
        // its ctor's ObservationProcessed subscription is live before any
        // observation ticks land.
        services.AddSingleton<LiveTagChangeRefreshTrigger>();

        // TTL provider — plugin can override before this call by registering
        // its own IRefreshTtlProvider (TryAddSingleton respects existing reg).
        services.TryAddSingleton<IRefreshTtlProvider, DefaultRefreshTtlProvider>();

        // Per-category enable policy. The settings-backed implementation
        // reads from ISettingsStore directly (no plugin-side bridge needed —
        // the toggles live in this module's own settings doc, not the
        // host's). Singleton so the live mutable cache is shared between
        // the queue service and the settings section.
        services.AddSingleton<RefreshCategoryPolicy>();
        services.AddSingleton<IRefreshCategoryPolicy>(sp => sp.GetRequiredService<RefreshCategoryPolicy>());

        services.AddSingleton<PlayerRefreshQueueService>();
        services.AddSingleton<IPlayerRefreshQueueService>(sp => sp.GetRequiredService<PlayerRefreshQueueService>());

        // Exhausted-row pruning used to live inline in the refresh-queue's
        // worker loop; D6 moved it to the shared DB-maintenance contributor
        // pattern so the cleanup window is centrally scheduled alongside
        // cache eviction and the weekly VACUUM/ANALYZE.
        services.AddMaintenanceContributor<RefreshQueueMaintenanceContributor>();

        // Cleanup for queue rows whose category was disabled via the
        // per-category policy. The worker already filters disabled rows
        // out of PickNextAsync; this contributor keeps the table bounded
        // when the user toggles a category off and never turns it back on.
        services.AddMaintenanceContributor<RefreshQueueDisabledCategoryPruneContributor>();

        // Read-only stats service for the queue diagnostics UI. Knows the
        // worker's WorkerGap / MaxAttempts so it can compute a realistic
        // drain-time ETA without duplicating those constants into the UI.
        services.AddSingleton<IRefreshQueueStatsService, RefreshQueueStatsService>();

        // Module-owned IAutoSettingsSection — contributes the refresh-queue
        // diagnostics tab to whichever plugin wires AddNexusKitUi() and the
        // AutoSettings window. The Strings.resx-backed localizer is added
        // alongside so the section's keys resolve without the plugin having
        // to know about this module's resources.
        services.AddResourceLocalizer<NexusKit.Modules.PlayerEnrichment.Resources.Strings>();
        services.AddSingleton<IAutoSettingsSection, RefreshQueueSettingsSection>();

        return services;
    }
}
